using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Serialization;
using Cortana.Plugins.Memory.Storage;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 连接宿主内部 conversation-feed 的最小后台服务：
/// - 握手 connected → subscribe → subscribed
/// - 接收 event 帧并通过记忆存储门面写入观察记录
/// </summary>
public sealed class MemoryIngestService(PluginSettings settings, ILogger<MemoryIngestService> logger, IMemoryStore store) : IHostedService
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, AssistantStreamBuffer> _assistantStreams = new(StringComparer.Ordinal);
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.EnsureInitialized();
        _loopTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        logger.LogInformation("MemoryIngestService 已启动，目标：{Endpoint}", settings.ConversationFeedEndpoint);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts.Cancel(); } catch { }
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        logger.LogInformation("MemoryIngestService 已停止");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (settings.ConversationFeedPort <= 0 && string.IsNullOrWhiteSpace(settings.ConversationFeedEndpoint))
        {
            logger.LogWarning("ConversationFeed 配置缺失（端口与 Endpoint 均为空），跳过连接。");
            return;
        }

        var endpoint = settings.ConversationFeedPort > 0
            ? $"ws://localhost:{settings.ConversationFeedPort}/internal/conversation-feed/"
            : settings.ConversationFeedEndpoint;
        var uri = new Uri(endpoint);
        using var ws = new ClientWebSocket();

        try
        {
            logger.LogInformation("连接 conversation-feed：{Uri}", uri);
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "连接 conversation-feed 失败：{Uri}", uri);
            return;
        }

        // 1) 期望先收到 connected
        if (!await ReadUntilTypeAsync(ws, "connected", ct).ConfigureAwait(false))
        {
            logger.LogWarning("未收到 connected 握手，终止。");
            await SafeCloseAsync(ws).ConfigureAwait(false);
            return;
        }

        // 2) 发送 subscribe
        var sub = new ConversationFeedSubscribeFrame
        {
            Type = "subscribe",
            Topics = ["conversation"],
            Protocol = "conversation-feed",
            Version = "1.0.0"
        };
        var json = JsonSerializer.Serialize(sub, MemoryInternalJsonContext.Default.ConversationFeedSubscribeFrame);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // 3) 等 subscribed
        if (!await ReadUntilTypeAsync(ws, "subscribed", ct).ConfigureAwait(false))
        {
            logger.LogWarning("未收到 subscribed 确认，终止。");
            await SafeCloseAsync(ws).ConfigureAwait(false);
            return;
        }

        // 3.5) 触发历史回放（since=0）
        var replay = new ConversationFeedReplayFrame { Type = "replay", SinceTimestamp = 0, BatchSize = 500 };
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replay, MemoryInternalJsonContext.Default.ConversationFeedReplayFrame)),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // 4) 主接收循环：batch 入库 + 实时事件最小入库
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var text = await ReadTextMessageAsync(ws, ct).ConfigureAwait(false);
                if (text is null) break;
                // 期待 { type: "event", topic: "conversation", eventType, payload }
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (!string.Equals(type, "event", StringComparison.Ordinal))
                    {
                        logger.LogDebug("忽略非 event 帧：{Text}", text);
                        continue;
                    }
                    var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() : null;
                    if (string.Equals(eventType, "conversation.export.batch", StringComparison.Ordinal))
                    {
                        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                        {
                            IngestExportBatch(payload);
                        }
                        continue;
                    }

                    // 实时事件中 assistant.delta 是流式片段，只参与内存聚合；turn.completed 才落库完整助手输出。
                    if (root.TryGetProperty("payload", out var live) && live.ValueKind == JsonValueKind.Object)
                    {
                        TryIngestLiveEvent(eventType, live);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "解析 feed 帧失败");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "feed 接收循环异常中止");
        }
        finally
        {
            await SafeCloseAsync(ws).ConfigureAwait(false);
        }
    }

    // 初始化迁移逻辑已下沉至 IMemoryStore

    private void IngestExportBatch(JsonElement payload)
    {
        if (!payload.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;

        var count = 0;
        var list = new List<ObservationRecord>();
        foreach (var it in items.EnumerateArray())
        {
            var r = new ObservationRecord
            {
                Id = it.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                AgentId = it.TryGetProperty("agentId", out var agEl) ? agEl.GetString() : null,
                AgentName = GetString(it, "agentName", "AgentName"),
                WorkspaceId = ResolveWorkspaceId(it),
                SessionId = it.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? string.Empty : string.Empty,
                TurnId = it.TryGetProperty("turnId", out var tEl) ? tEl.GetString() : null,
                MessageId = it.TryGetProperty("messageId", out var midEl) ? midEl.GetString() : null,
                EventType = it.TryGetProperty("eventType", out var etEl) ? etEl.GetString() : (it.TryGetProperty("type", out var t2) ? t2.GetString() : null),
                Role = it.TryGetProperty("role", out var roleEl) ? (roleEl.GetString() ?? string.Empty) : string.Empty,
                Content = it.TryGetProperty("content", out var cEl) && cEl.ValueKind != JsonValueKind.Null ? cEl.GetString() : null,
                AttachmentsJson = ResolveAttachments(it),
                CreatedTimestamp = it.TryGetProperty("createdTimestamp", out var tsEl) ? tsEl.GetInt64() : (it.TryGetProperty("timestamp", out var ts2) && ts2.ValueKind == JsonValueKind.Number ? ts2.GetInt64() : 0L),
                ModelName = it.TryGetProperty("modelName", out var mEl) && mEl.ValueKind != JsonValueKind.Null ? mEl.GetString() : null,
                TraceId = it.TryGetProperty("traceId", out var trEl) ? trEl.GetString() : (it.TryGetProperty("TraceId", out trEl) ? trEl.GetString() : null),
                SourceFactsJson = it.GetRawText(),
                CreatedAt = ToIsoTime(it.TryGetProperty("createdTimestamp", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number ? createdEl.GetInt64() : 0L)
            };
            list.Add(r);
            count++;
        }
        store.BulkInsertObservations(list);
        logger.LogInformation("导入历史批次 {Count} 条", count);
    }

    internal void TryIngestLiveEvent(string? eventType, JsonElement live)
    {
        string id;
        string sessionId = GetString(live, "sessionId", "SessionId") ?? string.Empty;
        string role;
        string? content = null;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string? model = GetString(live, "modelName", "ModelName", "modelId", "ModelId");

        if (string.Equals(eventType, "conversation.user.message", StringComparison.Ordinal))
        {
            role = "user";
            id = GetString(live, "userMessageId", "UserMessageId") ?? Guid.NewGuid().ToString("N");
            content = GetString(live, "content", "Content");
        }
        else if (string.Equals(eventType, "conversation.assistant.delta", StringComparison.Ordinal))
        {
            BufferAssistantDelta(live);
            return;
        }
        else if (string.Equals(eventType, "conversation.turn.completed", StringComparison.Ordinal))
        {
            role = "assistant";
            id = GetString(live, "assistantMessageId", "AssistantMessageId") ?? Guid.NewGuid().ToString("N");
            if (!IsSuccessfulTurnCompletion(live))
            {
                CompleteAssistantStream(live);
                return;
            }

            content = GetString(live, "assistantResponse", "AssistantResponse", "content", "Content") ?? CompleteAssistantStream(live);
            if (string.IsNullOrWhiteSpace(content)) return;
        }
        else
        {
            return;
        }

        string? agentId = GetString(live, "agentId", "AgentId");
        string? agentName = GetString(live, "agentName", "AgentName");
        string? turnId = GetString(live, "turnId", "TurnId");
        string? messageId = null;
        if (string.Equals(eventType, "conversation.user.message", StringComparison.Ordinal))
            messageId = GetString(live, "userMessageId", "UserMessageId");
        else if (string.Equals(eventType, "conversation.turn.completed", StringComparison.Ordinal))
            messageId = GetString(live, "assistantMessageId", "AssistantMessageId");

        var createdTimestamp = GetInt64(live, "occurredAt", "createdTimestamp", "timestamp") ?? ts;
        var record = new ObservationRecord
        {
            Id = id,
            AgentId = agentId,
            AgentName = agentName,
            WorkspaceId = ResolveWorkspaceId(live),
            SessionId = sessionId,
            TurnId = turnId,
            MessageId = messageId,
            EventType = eventType,
            Role = role,
            Content = content,
            AttachmentsJson = ResolveAttachments(live),
            CreatedTimestamp = createdTimestamp,
            ModelName = model,
            TraceId = GetString(live, "traceId", "TraceId"),
            SourceFactsJson = live.GetRawText(),
            CreatedAt = ToIsoTime(createdTimestamp)
        };

        store.InsertObservation(record);
    }

    private void BufferAssistantDelta(JsonElement live)
    {
        var messageId = GetString(live, "assistantMessageId", "AssistantMessageId");
        var delta = GetString(live, "delta", "Delta", "content", "Content");
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(delta)) return;

        var buffer = GetOrCreateAssistantStream(messageId);
        buffer.Append(delta);
    }

    private string? CompleteAssistantStream(JsonElement live)
    {
        var messageId = GetString(live, "assistantMessageId", "AssistantMessageId");
        if (string.IsNullOrEmpty(messageId)) return null;
        if (!_assistantStreams.Remove(messageId, out var buffer)) return null;
        return buffer.ToString();
    }

    private AssistantStreamBuffer GetOrCreateAssistantStream(string messageId)
    {
        if (_assistantStreams.TryGetValue(messageId, out var buffer)) return buffer;

        buffer = new AssistantStreamBuffer();
        _assistantStreams[messageId] = buffer;
        return buffer;
    }

    private static bool IsSuccessfulTurnCompletion(JsonElement live)
    {
        var status = GetString(live, "status", "Status");
        return string.IsNullOrWhiteSpace(status)
            || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveWorkspaceId(JsonElement el)
    {
        if (el.TryGetProperty("workspaceId", out var workspace) || el.TryGetProperty("WorkspaceId", out workspace))
        {
            return workspace.GetString();
        }

        return string.IsNullOrWhiteSpace(settings.WorkspaceDirectory) ? null : settings.WorkspaceDirectory;
    }

    private static string ToIsoTime(long timestamp)
    {
        return timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("O")
            : DateTimeOffset.UtcNow.ToString("O");
    }

    private static string ResolveAttachments(JsonElement el)
    {
        if (el.TryGetProperty("attachments", out var a) || el.TryGetProperty("Attachments", out a) || el.TryGetProperty("assets", out a) || el.TryGetProperty("Assets", out a))
        {
            if (a.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return a.GetRawText();
        }
        return "[]";
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }

        return null;
    }

    private static long? GetInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        }

        return null;
    }

    private static async Task<string?> ReadTextMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.Count > 0) message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task<bool> ReadUntilTypeAsync(ClientWebSocket ws, string targetType, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            while (true)
            {
                var text = await ReadTextMessageAsync(ws, timeoutCts.Token).ConfigureAwait(false);
                if (text is null) return false;
                var ok = TryGetType(text, out var type) && string.Equals(type, targetType, StringComparison.Ordinal);
                if (ok) return true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 超时
        }
    }

    private static bool TryGetType(string json, out string? type)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            return true;
        }
        catch
        {
            type = null; return false;
        }
    }

    private static async Task SafeCloseAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private sealed class AssistantStreamBuffer
    {
        private readonly StringBuilder _content = new();

        public void Append(string delta) => _content.Append(delta);

        public override string ToString() => _content.ToString();
    }
}