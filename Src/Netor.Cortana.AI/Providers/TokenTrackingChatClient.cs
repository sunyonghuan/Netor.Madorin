using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using System.Collections;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 跟踪对话 Token 使用情况的聊天客户端包装器。
/// </summary>
public partial class TokenTrackingChatClient : DelegatingChatClient
{
    private const string TraceEnabledEnvironmentVariable = "CORTANA_AI_TRACE_ENABLED";

    /// <summary>最近一次调用的输入 token 数（= 当前上下文窗口占用）</summary>
    public long LastInputTokens => Volatile.Read(ref _lastInputTokens);

    /// <summary>本次对话累计输出 token（用于成本估算）</summary>
    public long TotalOutputTokens => Volatile.Read(ref _totalOutputTokens);

    /// <summary>模型上下文窗口最大值</summary>
    public long MaxContextTokens { get; } = 128000;

    /// <summary>上下文使用比例 0.0 ~ 1.0</summary>
    public double ContextUsageRatio =>
        MaxContextTokens > 0 ? (double)LastInputTokens / MaxContextTokens : 0;

    private long _lastInputTokens;
    private long _totalOutputTokens;
    private StringBuilder _lastAssistantReasoning = new();
    private readonly bool _enableReasoning;
    private volatile bool _requireReasoningPassback;
    private readonly IAppPaths? _appPaths;
    private readonly bool _traceEnabled;
    private readonly SystemSettingsService? _systemSettings;

    /// <summary>
    /// Usage 上报抑制计数（支持嵌套）。>0 时 <see cref="RecordUsage"/> 直接忽略。
    /// 用于压缩/标题生成等"后台 LLM 调用"期间，防止它们的 usage 覆盖主对话的进度条。
    /// </summary>
    private int _suppressDepth;

    /// <summary>
    /// 用量观察者：每当本 Client 收到 <see cref="UsageDetails"/> 时会被回调。
    /// 用于把 token 状态上报到工厂/外层持久容器，使 UI 显示不随 ChatClient 重建而丢失。
    /// </summary>
    private readonly Action<UsageDetails>? _usageObserver;

    /// <summary>
    /// 初始化 <see cref="TokenTrackingChatClient"/> 的新实例。
    /// </summary>
    /// <param name="innerClient">内部聊天客户端。</param>
    /// <param name="maxContextTokens">模型支持的最大上下文 Token 数。</param>
    /// <param name="usageObserver">Token 用量观察回调。</param>
    internal TokenTrackingChatClient(
        IChatClient innerClient,
        long maxContextTokens,
        Action<UsageDetails>? usageObserver = null,
        bool enableReasoning = false,
        IAppPaths? appPaths = null,
        SystemSettingsService? systemSettings = null)
        : base(innerClient)
    {
        MaxContextTokens = maxContextTokens <= 0 ? 128000 : maxContextTokens;
        _usageObserver = usageObserver;
        _enableReasoning = enableReasoning;
        _appPaths = appPaths;
        _systemSettings = systemSettings;
        _traceEnabled = ResolveTraceEnabled(systemSettings);
    }

    /// <summary>
    /// 标记本模型在当前会话需要强制回传 reasoning（例如服务端返回 400 要求）。
    /// </summary>
    public void RequireReasoningPassback() => _requireReasoningPassback = true;

    /// <summary>
    /// 重置最近一次输入 Token 和累计输出 Token 计数。
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastInputTokens, 0);
        Interlocked.Exchange(ref _totalOutputTokens, 0);
    }

    /// <summary>
    /// 开启一个作用域：在作用域内的所有 LLM 调用产生的 UsageDetails 都会被忽略，
    /// 不会覆盖 <see cref="LastInputTokens"/>，也不会触发 observer。
    /// 典型场景：历史压缩、会话标题生成等后台调用借用本 ChatClient，但其 token 不应显示到主进度条。
    /// </summary>
    /// <returns>用于结束抑制作用域的对象。</returns>
    public IDisposable SuppressUsage() => new SuppressScope(this);

    private sealed class SuppressScope : IDisposable
    {
        private readonly TokenTrackingChatClient _owner;
        private int _disposed;

        /// <summary>
        /// 初始化 <see cref="SuppressScope"/> 的新实例，并进入抑制作用域。
        /// </summary>
        /// <param name="owner">所属的 Token 跟踪客户端。</param>
        public SuppressScope(TokenTrackingChatClient owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._suppressDepth);
        }

        /// <summary>
        /// 释放抑制作用域，并恢复 Usage 上报。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _owner._suppressDepth);
        }
    }

    /// <summary>
    /// 获取非流式对话响应，并记录本次调用的 Token 用量。
    /// </summary>
    /// <param name="messages">对话消息集合。</param>
    /// <param name="options">聊天选项。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对话响应结果。</returns>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var requestId = CreateTraceId();
        var outboundMessages = ((_enableReasoning || _requireReasoningPassback || _lastAssistantReasoning.Length > 0)
            ? EnsureReasoningPassed(messages)
            : messages).ToList();

        WriteTraceLog(requestId, "request", outboundMessages, options, null);

        var response = await base.GetResponseAsync(
            outboundMessages,
            options, ct);
        WriteTraceLog(requestId, "response", null, options, response);
        RecordUsage(response.Usage);
        return response;
    }

    /// <summary>
    /// 获取流式对话响应，并在流结束后统一记录 Token 用量。
    /// </summary>
    /// <param name="messages">对话消息集合。</param>
    /// <param name="options">聊天选项。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流式对话响应序列。</returns>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestId = CreateTraceId();
        var outboundMessages = messages.ToList();
        WriteTraceLog(requestId, "stream-request", outboundMessages, options, null);

        // 流式期间累积 usage：取 InputToken 的最大值、OutputToken 累加，流结束统一提交一次
        long? pendingInput = null;
        long pendingOutput = 0;
        UsageDetails? lastUsage = null;
        var responseText = new StringBuilder();
        var responseUpdates = 0;
        var updateSnapshots = new List<AiTraceMessage>();
        //_lastAssistantReasoning.Clear();
        //await foreach (var update in base.GetStreamingResponseAsync(
        //    (_enableReasoning || _requireReasoningPassback || _lastAssistantReasoning.Length > 0)
        //        ? EnsureReasoningPassed(messages) : messages, options, ct))
        await using var enumerator = base.GetStreamingResponseAsync(outboundMessages, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ChatResponseUpdate update;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteTraceLog(requestId, "stream-error", outboundMessages, options, null,
                    responseText.ToString(), responseUpdates, lastUsage, updateSnapshots, ex);
                throw;
            }

            responseUpdates++;
            updateSnapshots.Add(DescribeStreamingUpdate(responseUpdates, update));

            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    var details = usage.Details;
                    if (details.InputTokenCount is { } i && (pendingInput is null || i > pendingInput))
                        pendingInput = i;
                    if (details.OutputTokenCount is { } o)
                        pendingOutput += o;
                    lastUsage = details;
                }
                else if (content is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    // 捕获最近一条 assistant 的 reasoning 内容，供同轮工具二次调用时回传
                    _lastAssistantReasoning.Append(reasoning.Text);
                }
                else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                {
                    responseText.Append(text.Text);
                }
            }

            yield return update;
        }

        WriteTraceLog(requestId, "stream-response", null, options, null, responseText.ToString(), responseUpdates, lastUsage, updateSnapshots);

        if (lastUsage is not null)
        {
            // 构造合并后的 UsageDetails 统一上报一次
            var merged = new UsageDetails
            {
                InputTokenCount = pendingInput ?? lastUsage.InputTokenCount,
                OutputTokenCount = pendingOutput > 0 ? pendingOutput : lastUsage.OutputTokenCount,
                TotalTokenCount = lastUsage.TotalTokenCount,
                AdditionalCounts = lastUsage.AdditionalCounts
            };
            RecordUsage(merged);
        }
    }

    /// <summary>
    /// 确保需要回传 reasoning 的消息包含对应的推理内容。
    /// </summary>
    /// <param name="messages">原始消息集合。</param>
    /// <returns>补齐 reasoning 后的消息集合。</returns>
    private IEnumerable<ChatMessage> EnsureReasoningPassed(IEnumerable<ChatMessage> messages)
    {
        // 若上一轮（同一 Run 流程内）模型给出了 reasoning，需要在包含 tool_calls 的 assistant 消息上回传
        // 以满足部分思维模型的协议要求。
        var listInput = messages?.ToList() ?? [];
        if (_lastAssistantReasoning.Length <= 0)
        {
            foreach (var m in listInput) yield return m;
            yield break;
        }
        var results = new List<ChatMessage>(messages!)
        {
            new(ChatRole.Assistant, string.Empty)
            {
                 Contents=[new TextReasoningContent(_lastAssistantReasoning.ToString())]
            }
        };
        foreach (var m in results) yield return m;
        yield break;
        //// 1) 针对包含 tool_calls 但缺失 reasoning 的 assistant：注入 reasoning
        //for (var i = 0; i < listInput.Count; i++)
        //{
        //    var m = listInput[i];
        //    if (m.Role != ChatRole.Assistant) continue;
        //    var hasReasoning = m.Contents?.Any(c => c is TextReasoningContent) == true;
        //    var hasToolCall = m.Contents?.Any(c => c is ToolCallContent or McpServerToolCallContent) == true;
        //    if (hasToolCall && !hasReasoning)
        //    {
        //        var c = m.Contents is null ? [] : new List<AIContent>(m.Contents);
        //        c.Insert(0, new TextReasoningContent(_lastAssistantReasoning.ToString()));
        //        listInput[i] = new ChatMessage { Role = m.Role, AuthorName = m.AuthorName, MessageId = m.MessageId, CreatedAt = m.CreatedAt, Contents = c };
        //    }
        //}

        //// 2) 兜底：若“最近一条 assistant”仍缺 reasoning，也补上（覆盖“无 tool_calls”场景）
        //for (int i = listInput.Count - 1; i >= 0; i--)
        //{
        //    var m = listInput[i];
        //    if (m.Role != ChatRole.Assistant) continue;
        //    var hasReasoning = m.Contents?.Any(c => c is TextReasoningContent) == true;
        //    if (!hasReasoning)
        //    {
        //        var c = m.Contents is null ? [] : new List<AIContent>(m.Contents);
        //        c.Insert(0, new TextReasoningContent(_lastAssistantReasoning.ToString()));
        //        listInput[i] = new ChatMessage { Role = m.Role, AuthorName = m.AuthorName, MessageId = m.MessageId, CreatedAt = m.CreatedAt, Contents = c };
        //    }
        //    break; // 只处理最近一条 assistant
        //}

        //foreach (var m in listInput) yield return m;
    }

    private static string CreateTraceId() => DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];

    private void WriteTraceLog(
        string requestId,
        string stage,
        IReadOnlyList<ChatMessage>? messages,
        ChatOptions? options,
        ChatResponse? response,
        string? streamingText = null,
        int? streamingUpdates = null,
        UsageDetails? streamingUsage = null,
        IReadOnlyList<AiTraceMessage>? updateSnapshots = null,
        Exception? exception = null)
    {
        if (!_traceEnabled)
        {
            return;
        }

        try
        {
            var logRoot = Environment.GetEnvironmentVariable("CORTANA_AI_TRACE_DIR");
            if (string.IsNullOrWhiteSpace(logRoot))
            {
                var workspace = _appPaths?.WorkspaceDirectory;
                logRoot = string.IsNullOrWhiteSpace(workspace)
                    ? Path.Combine(AppContext.BaseDirectory, "logs", "ai-traces")
                    : Path.Combine(workspace, ".cortana", "logs", "ai-traces");
            }

            Directory.CreateDirectory(logRoot);
            var filePath = Path.Combine(logRoot, $"ai-{requestId}-{stage}.json");
            var payload = new AiTracePayload
            {
                RequestId = requestId,
                Stage = stage,
                Timestamp = DateTimeOffset.Now,
                Options = DescribeOptions(options),
                Messages = messages?.Select((m, i) => DescribeMessage(i, m)).ToList(),
                Response = response is null ? null : DescribeResponse(response),
                StreamingText = streamingText,
                StreamingUpdateCount = streamingUpdates,
                StreamingUsage = streamingUsage is null ? null : DescribeUsage(streamingUsage),
                StreamingUpdates = updateSnapshots?.ToList(),
                ProtocolDiagnostics = messages is null ? null : AnalyzeMessages(messages),
                Error = exception is null ? null : DescribeException(exception)
            };

            var json = JsonSerializer.Serialize(payload, AiTraceJsonContext.Default.AiTracePayload);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }
        catch
        {
            // 诊断日志不能影响主对话流程。
        }
    }

    private static AiTraceMessage DescribeMessage(int index, ChatMessage message) => new()
    {
        Index = index,
        Role = message.Role.ToString(),
        AuthorName = message.AuthorName,
        MessageId = message.MessageId,
        CreatedAt = message.CreatedAt,
        Text = message.Text,
        Contents = message.Contents.Select((c, i) => DescribeContent(i, c)).ToList()
    };

    private static AiTraceMessage DescribeStreamingUpdate(int index, ChatResponseUpdate update) => new()
    {
        Index = index,
        Role = "update",
        AuthorName = update.AuthorName,
        MessageId = update.MessageId,
        CreatedAt = update.CreatedAt,
        Text = update.Text,
        Contents = update.Contents.Select((c, i) => DescribeContent(i, c)).ToList()
    };

    private static AiTraceContent DescribeContent(int index, AIContent content) => content switch
    {
        TextContent text => new AiTraceContent { Index = index, Kind = "text", Text = text.Text },
        TextReasoningContent reasoning => new AiTraceContent { Index = index, Kind = "reasoning", Text = reasoning.Text, ProtectedData = reasoning.ProtectedData },
        FunctionCallContent functionCall => new AiTraceContent { Index = index, Kind = "functionCall", CallId = functionCall.CallId, Name = functionCall.Name, ArgumentsJson = SerializeObject(functionCall.Arguments) },
        FunctionResultContent functionResult => new AiTraceContent { Index = index, Kind = "functionResult", CallId = functionResult.CallId, ResultJson = SerializeObject(functionResult.Result), ExceptionMessage = functionResult.Exception?.ToString() },
        McpServerToolCallContent mcpCall => new AiTraceContent { Index = index, Kind = "mcpToolCall", CallId = mcpCall.CallId, Name = mcpCall.Name, ServerName = mcpCall.ServerName, ArgumentsJson = SerializeObject(mcpCall.Arguments) },
        ToolCallContent toolCall => new AiTraceContent { Index = index, Kind = "toolCall", CallId = toolCall.CallId },
        McpServerToolResultContent mcpResult => new AiTraceContent { Index = index, Kind = "mcpToolResult", CallId = mcpResult.CallId, ResultJson = SerializeObject(mcpResult.Outputs) },
        ToolResultContent toolResult => new AiTraceContent { Index = index, Kind = "toolResult", CallId = toolResult.CallId },
        DataContent data => new AiTraceContent { Index = index, Kind = "data", MediaType = data.MediaType, Uri = data.Uri?.ToString(), DataLength = data.Data.Length },
        UsageContent usage => new AiTraceContent { Index = index, Kind = "usage", ResultJson = SerializeObject(DescribeUsage(usage.Details)) },
        _ => new AiTraceContent { Index = index, Kind = content.GetType().FullName ?? content.GetType().Name, Text = content.ToString() }
    };

    private static AiTraceResponse DescribeResponse(ChatResponse response) => new()
    {
        Text = response.Text,
        MessageCount = response.Messages.Count,
        Messages = response.Messages.Select((m, i) => DescribeMessage(i, m)).ToList(),
        Usage = response.Usage is null ? null : DescribeUsage(response.Usage)
    };

    private static AiTraceUsage DescribeUsage(UsageDetails usage) => new()
    {
        InputTokenCount = usage.InputTokenCount,
        OutputTokenCount = usage.OutputTokenCount,
        TotalTokenCount = usage.TotalTokenCount,
        AdditionalCountsJson = SerializeObject(usage.AdditionalCounts)
    };

    private static string? DescribeOptions(ChatOptions? options) => options is null ? null : SerializeObject(options);

    private static string? SerializeObject(object? value)
    {
        if (value is null) return null;
        try
        {
            return value switch
            {
                string text => text,
                JsonElement json => json.GetRawText(),
                IDictionary<string, object?> dictionary => SerializeDictionary(dictionary),
                IDictionary<string, JsonElement> jsonDictionary => JsonSerializer.Serialize(
                    jsonDictionary,
                    AiTraceJsonContext.Default.DictionaryStringJsonElement),
                IEnumerable enumerable when value is not string => SerializeEnumerable(enumerable),
                _ => SerializeScalar(value)
            };
        }
        catch
        {
            return value.ToString();
        }
    }

    private static string SerializeDictionary(IDictionary<string, object?> dictionary)
    {
        var normalized = new Dictionary<string, JsonElement>(dictionary.Count, StringComparer.Ordinal);
        foreach (var pair in dictionary)
        {
            normalized[pair.Key] = NormalizeTraceValue(pair.Value);
        }

        return JsonSerializer.Serialize(normalized, AiTraceJsonContext.Default.DictionaryStringJsonElement);
    }

    private static string SerializeEnumerable(IEnumerable enumerable)
    {
        var values = new List<JsonElement>();
        foreach (var item in enumerable)
        {
            values.Add(NormalizeTraceValue(item));
        }

        return JsonSerializer.Serialize(values, AiTraceJsonContext.Default.ListJsonElement);
    }

    private static string SerializeScalar(object value)
    {
        var json = NormalizeTraceValue(value);
        return json.GetRawText();
    }

    private static JsonElement NormalizeTraceValue(object? value)
    {
        if (value is null) return JsonDocument.Parse("null").RootElement.Clone();
        if (value is JsonElement json) return json.Clone();

        var raw = value switch
        {
            string text => JsonSerializer.Serialize(text, AiTraceJsonContext.Default.String),
            bool boolean => boolean ? "true" : "false",
            byte number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sbyte number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ushort number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ulong number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IDictionary<string, object?> dictionary => SerializeDictionary(dictionary),
            IDictionary<string, JsonElement> jsonDictionary => JsonSerializer.Serialize(
                jsonDictionary,
                AiTraceJsonContext.Default.DictionaryStringJsonElement),
            IEnumerable enumerable when value is not string => SerializeEnumerable(enumerable),
            _ => JsonSerializer.Serialize(value.ToString(), AiTraceJsonContext.Default.String)
        };

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static bool ResolveTraceEnabled(SystemSettingsService? systemSettings)
    {
        var environmentValue = Environment.GetEnvironmentVariable(TraceEnabledEnvironmentVariable);
        if (bool.TryParse(environmentValue, out var enabledFromEnvironment))
        {
            return enabledFromEnvironment;
        }

        if (systemSettings is not null)
        {
            return systemSettings.GetValue("AI.Trace.Enabled", false);
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static AiTraceProtocolDiagnostics AnalyzeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var diagnostics = new AiTraceProtocolDiagnostics();
        var toolCallOwnerByCallId = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == ChatRole.Assistant)
            {
                var callIds = GetToolCallIds(message).ToList();
                if (callIds.Count > 0)
                {
                    diagnostics.AssistantToolCallMessages++;
                    foreach (var callId in callIds)
                    {
                        toolCallOwnerByCallId.TryAdd(callId, index);
                    }
                }
            }

            if (message.Role == ChatRole.Tool)
            {
                diagnostics.ToolMessages++;
                var callIds = GetToolResultCallIds(message).ToList();
                if (callIds.Count == 0)
                {
                    diagnostics.OrphanToolMessages.Add(new AiTraceProtocolIssue
                    {
                        MessageIndex = index,
                        MessageId = message.MessageId,
                        Reason = "tool message does not contain any call id"
                    });
                    continue;
                }

                foreach (var callId in callIds)
                {
                    if (!toolCallOwnerByCallId.TryGetValue(callId, out var assistantIndex))
                    {
                        diagnostics.OrphanToolMessages.Add(new AiTraceProtocolIssue
                        {
                            MessageIndex = index,
                            MessageId = message.MessageId,
                            CallId = callId,
                            Reason = "no preceding assistant tool_call found"
                        });
                        continue;
                    }

                    if (index != assistantIndex + 1)
                    {
                        diagnostics.NonAdjacentToolMessages.Add(new AiTraceProtocolIssue
                        {
                            MessageIndex = index,
                            MessageId = message.MessageId,
                            CallId = callId,
                            RelatedAssistantIndex = assistantIndex,
                            Reason = "tool message is not adjacent to its assistant tool_call"
                        });
                    }
                }
            }
        }

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var callIds = GetToolCallIds(message).ToList();
            if (callIds.Count == 0)
            {
                continue;
            }

            foreach (var callId in callIds)
            {
                var matched = false;
                for (var candidateIndex = index + 1; candidateIndex < messages.Count; candidateIndex++)
                {
                    var candidate = messages[candidateIndex];
                    if (candidate.Role != ChatRole.Tool)
                    {
                        if (candidateIndex == index + 1)
                        {
                            break;
                        }
                        continue;
                    }

                    if (GetToolResultCallIds(candidate).Contains(callId, StringComparer.Ordinal))
                    {
                        matched = true;
                        break;
                    }

                    if (candidateIndex == index + 1)
                    {
                        break;
                    }
                }

                if (!matched)
                {
                    diagnostics.MissingToolResponses.Add(new AiTraceProtocolIssue
                    {
                        MessageIndex = index,
                        MessageId = message.MessageId,
                        CallId = callId,
                        Reason = "assistant tool_call does not have an adjacent tool response"
                    });
                }
            }
        }

        return diagnostics;
    }

    private static IEnumerable<string> GetToolCallIds(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            var callId = content switch
            {
                FunctionCallContent functionCall => functionCall.CallId,
                McpServerToolCallContent mcpToolCall => mcpToolCall.CallId,
                ToolCallContent toolCall => toolCall.CallId,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(callId))
            {
                yield return callId;
            }
        }
    }

    private static IEnumerable<string> GetToolResultCallIds(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            var callId = content switch
            {
                FunctionResultContent functionResult => functionResult.CallId,
                McpServerToolResultContent mcpToolResult => mcpToolResult.CallId,
                ToolResultContent toolResult => toolResult.CallId,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(callId))
            {
                yield return callId;
            }
        }
    }

    private static AiTraceError DescribeException(Exception exception) => new()
    {
        Type = exception.GetType().FullName ?? exception.GetType().Name,
        Message = exception.Message,
        StackTrace = exception.ToString()
    };

    private sealed partial class AiTracePayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public string? Options { get; set; }
        public List<AiTraceMessage>? Messages { get; set; }
        public AiTraceResponse? Response { get; set; }
        public string? StreamingText { get; set; }
        public int? StreamingUpdateCount { get; set; }
        public AiTraceUsage? StreamingUsage { get; set; }
        public List<AiTraceMessage>? StreamingUpdates { get; set; }
        public AiTraceProtocolDiagnostics? ProtocolDiagnostics { get; set; }
        public AiTraceError? Error { get; set; }
    }

    private sealed partial class AiTraceMessage
    {
        public int Index { get; set; }
        public string Role { get; set; } = string.Empty;
        public string? AuthorName { get; set; }
        public string? MessageId { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string? Text { get; set; }
        public List<AiTraceContent> Contents { get; set; } = [];
    }

    private sealed partial class AiTraceContent
    {
        public int Index { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string? Text { get; set; }
        public string? ProtectedData { get; set; }
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string? ServerName { get; set; }
        public string? ArgumentsJson { get; set; }
        public string? ResultJson { get; set; }
        public string? ExceptionMessage { get; set; }
        public string? MediaType { get; set; }
        public string? Uri { get; set; }
        public int? DataLength { get; set; }
    }

    private sealed partial class AiTraceResponse
    {
        public string? Text { get; set; }
        public int MessageCount { get; set; }
        public List<AiTraceMessage> Messages { get; set; } = [];
        public AiTraceUsage? Usage { get; set; }
    }

    private sealed partial class AiTraceUsage
    {
        public long? InputTokenCount { get; set; }
        public long? OutputTokenCount { get; set; }
        public long? TotalTokenCount { get; set; }
        public string? AdditionalCountsJson { get; set; }
    }

    private sealed partial class AiTraceProtocolDiagnostics
    {
        public int AssistantToolCallMessages { get; set; }
        public int ToolMessages { get; set; }
        public List<AiTraceProtocolIssue> OrphanToolMessages { get; set; } = [];
        public List<AiTraceProtocolIssue> MissingToolResponses { get; set; } = [];
        public List<AiTraceProtocolIssue> NonAdjacentToolMessages { get; set; } = [];
    }

    private sealed partial class AiTraceProtocolIssue
    {
        public int MessageIndex { get; set; }
        public string? MessageId { get; set; }
        public string? CallId { get; set; }
        public int? RelatedAssistantIndex { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    private sealed partial class AiTraceError
    {
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AiTracePayload))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(List<JsonElement>))]
    private sealed partial class AiTraceJsonContext : JsonSerializerContext;

    /// <summary>
    /// 记录 Usage 统计信息，并在需要时通知外部观察者。
    /// </summary>
    /// <param name="usage">本次调用的 Usage 信息。</param>
    private void RecordUsage(UsageDetails? usage)
    {
        if (usage is null) return;
        if (Volatile.Read(ref _suppressDepth) > 0) return;

        // 输入 token：覆盖（取最新值，因为它反映当前上下文大小）
        Interlocked.Exchange(ref _lastInputTokens, usage.InputTokenCount ?? 0);
        // 输出 token：累加（用于成本）
        Interlocked.Add(ref _totalOutputTokens, usage.OutputTokenCount ?? 0);

        // 上报到外部观察者（若有）
        try { _usageObserver?.Invoke(usage); }
        catch { /* 观察者异常不影响主流程 */ }
    }
}
