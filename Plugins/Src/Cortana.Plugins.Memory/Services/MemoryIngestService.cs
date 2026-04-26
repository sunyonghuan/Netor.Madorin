using Cortana.Plugins.Memory.Models;
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
        var sub = new
        {
            type = "subscribe",
            topics = new[] { "conversation" },
            protocol = "conversation-feed",
            version = "1.0.0"
        };
        var json = JsonSerializer.Serialize(sub);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // 3) 等 subscribed
        if (!await ReadUntilTypeAsync(ws, "subscribed", ct).ConfigureAwait(false))
        {
            logger.LogWarning("未收到 subscribed 确认，终止。");
            await SafeCloseAsync(ws).ConfigureAwait(false);
            return;
        }

        // 3.5) 触发历史回放（since=0）
        var replay = new { type = "replay", sinceTimestamp = 0, batchSize = 500 };
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replay)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // 4) 主接收循环：batch 入库 + 实时事件最小入库
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
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

                    // 简化：对实时事件，我们只在 user.message/assistant.delta/turn.completed 场景尝试最小入库
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

    private void TryIngestLiveEvent(string? eventType, JsonElement live)
    {
        // 只处理最小列：为实时消息生成一个临时 id，用 turnId+seq 或 messageId 组合。
        string id;
        string sessionId = live.TryGetProperty("SessionId", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        string role;
        string? content = null;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string? model = live.TryGetProperty("ModelName", out var mn) ? mn.GetString() : null;

        if (string.Equals(eventType, "conversation.user.message", StringComparison.Ordinal))
        {
            role = "user";
            id = live.TryGetProperty("UserMessageId", out var um) ? (um.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N");
            content = live.TryGetProperty("Content", out var c) ? c.GetString() : null;
        }
        else if (string.Equals(eventType, "conversation.assistant.delta", StringComparison.Ordinal))
        {
            role = "assistant";
            var aid = live.TryGetProperty("AssistantMessageId", out var am) ? (am.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N");
            var seq = live.TryGetProperty("Sequence", out var q) ? q.GetInt32() : 0;
            id = $"{aid}_{seq:000000}";
            content = live.TryGetProperty("Delta", out var d) ? d.GetString() : null;
        }
        else if (string.Equals(eventType, "conversation.turn.completed", StringComparison.Ordinal))
        {
            role = "assistant";
            id = live.TryGetProperty("AssistantMessageId", out var am) ? (am.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N");
            content = live.TryGetProperty("AssistantResponse", out var ar) ? ar.GetString() : null;
        }
        else
        {
            return;
        }

        string? agentId = live.TryGetProperty("AgentId", out var ag) ? ag.GetString() : (live.TryGetProperty("agentId", out ag) ? ag.GetString() : null);
        string? turnId = live.TryGetProperty("TurnId", out var tn) ? tn.GetString() : (live.TryGetProperty("turnId", out tn) ? tn.GetString() : null);
        string? messageId = null;
        if (string.Equals(eventType, "conversation.user.message", StringComparison.Ordinal))
            messageId = live.TryGetProperty("UserMessageId", out var um2) ? um2.GetString() : null;
        else if (string.Equals(eventType, "conversation.assistant.delta", StringComparison.Ordinal) || string.Equals(eventType, "conversation.turn.completed", StringComparison.Ordinal))
            messageId = live.TryGetProperty("AssistantMessageId", out var am2) ? am2.GetString() : null;

        var record = new ObservationRecord
        {
            Id = id,
            AgentId = agentId,
            WorkspaceId = ResolveWorkspaceId(live),
            SessionId = sessionId,
            TurnId = turnId,
            MessageId = messageId,
            EventType = eventType,
            Role = role,
            Content = content,
            AttachmentsJson = ResolveAttachments(live),
            CreatedTimestamp = live.TryGetProperty("createdTimestamp", out var tsLive) && tsLive.ValueKind == JsonValueKind.Number ? tsLive.GetInt64() : (live.TryGetProperty("timestamp", out tsLive) && tsLive.ValueKind == JsonValueKind.Number ? tsLive.GetInt64() : ts),
            ModelName = model,
            TraceId = live.TryGetProperty("TraceId", out var tr) ? tr.GetString() : (live.TryGetProperty("traceId", out tr) ? tr.GetString() : null),
            SourceFactsJson = live.GetRawText(),
            CreatedAt = ToIsoTime(ts)
        };

        store.InsertObservation(record);
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

    private static async Task<bool> ReadUntilTypeAsync(ClientWebSocket ws, string targetType, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
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
}