using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;
using System.Linq;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 跟踪对话 Token 使用情况的聊天客户端包装器。
/// </summary>
public class TokenTrackingChatClient : DelegatingChatClient
{
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
    private string? _lastAssistantReasoning;
    private readonly bool _enableReasoning;

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
        bool enableReasoning = false)
        : base(innerClient)
    {
        MaxContextTokens = maxContextTokens <= 0 ? 128000 : maxContextTokens;
        _usageObserver = usageObserver;
        _enableReasoning = enableReasoning;
    }

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
        var response = await base.GetResponseAsync(
            _enableReasoning ? EnsureReasoningPassed(messages) : messages,
            options, ct);
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
        // 流式期间累积 usage：取 InputToken 的最大值、OutputToken 累加，流结束统一提交一次
        long? pendingInput = null;
        long pendingOutput = 0;
        UsageDetails? lastUsage = null;

        await foreach (var update in base.GetStreamingResponseAsync(
            _enableReasoning ? EnsureReasoningPassed(messages) : messages, options, ct))
        {
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
                else if (_enableReasoning && content is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    // 捕获最近一条 assistant 的 reasoning 内容，供同轮工具二次调用时回传
                    _lastAssistantReasoning = reasoning.Text;
                }
            }
            yield return update;
        }

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
        var listInput = messages?.ToList() ?? new List<ChatMessage>(0);
        if (string.IsNullOrWhiteSpace(_lastAssistantReasoning))
        {
            foreach (var m in listInput) yield return m;
            yield break;
        }

        // 1) 针对包含 tool_calls 但缺失 reasoning 的 assistant：注入 reasoning
        for (var i = 0; i < listInput.Count; i++)
        {
            var m = listInput[i];
            if (m.Role != ChatRole.Assistant) continue;
            var hasReasoning = m.Contents?.Any(c => c is TextReasoningContent) == true;
            var hasToolCall = m.Contents?.Any(c => c is ToolCallContent || c is McpServerToolCallContent) == true;
            if (hasToolCall && !hasReasoning)
            {
                var c = m.Contents is null ? new List<AIContent>() : new List<AIContent>(m.Contents);
                c.Insert(0, new TextReasoningContent(_lastAssistantReasoning));
                listInput[i] = new ChatMessage { Role = m.Role, AuthorName = m.AuthorName, MessageId = m.MessageId, CreatedAt = m.CreatedAt, Contents = c };
            }
        }

        // 2) 兜底：若“最近一条 assistant”仍缺 reasoning，也补上（覆盖“无 tool_calls”场景）
        for (int i = listInput.Count - 1; i >= 0; i--)
        {
            var m = listInput[i];
            if (m.Role != ChatRole.Assistant) continue;
            var hasReasoning = m.Contents?.Any(c => c is TextReasoningContent) == true;
            if (!hasReasoning)
            {
                var c = m.Contents is null ? new List<AIContent>() : new List<AIContent>(m.Contents);
                c.Insert(0, new TextReasoningContent(_lastAssistantReasoning));
                listInput[i] = new ChatMessage { Role = m.Role, AuthorName = m.AuthorName, MessageId = m.MessageId, CreatedAt = m.CreatedAt, Contents = c };
            }
            break; // 只处理最近一条 assistant
        }

        foreach (var m in listInput) yield return m;
    }

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