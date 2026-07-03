using System.IO;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus 上 plugin->plugin 方向 type=event 的异步广播事件帧。
/// 由 WebSocketPluginBusServerService.HandleMessageAsync 在识别到事件帧后调用。
/// host->plugin 方向的 event 帧由 BroadcastPluginBusAsync 直接处理，不经本类。
/// 详见 docs/已完成功能规划/插件事件广播/README.md §2.4 与 03-宿主转发器实现.md §二。
/// </summary>
internal sealed class PluginBusBroadcastDispatcher
{
    private const int MaxHopCount = 8;
    private const int PerSubscriberTimeoutMs = 1000;
    private const int MaxFrameBytes = 1 * 1024 * 1024;

    private readonly PluginBusSubscriptionRegistry _subscriptions;
    private readonly PluginManifestRegistry _manifests;
    private readonly VoiceEventBridge _voiceBridge;
    private readonly WebSocketConnectionManager _pluginBusConnections;
    private readonly Func<string, string, CancellationToken, Task> _sendAsync;
    private readonly ILogger _logger;

    public PluginBusBroadcastDispatcher(
        PluginBusSubscriptionRegistry subscriptions,
        PluginManifestRegistry manifests,
        VoiceEventBridge voiceBridge,
        WebSocketConnectionManager pluginBusConnections,
        Func<string, string, CancellationToken, Task> sendAsync,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(voiceBridge);
        ArgumentNullException.ThrowIfNull(pluginBusConnections);
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptions = subscriptions;
        _manifests = manifests;
        _voiceBridge = voiceBridge;
        _pluginBusConnections = pluginBusConnections;
        _sendAsync = sendAsync;
        _logger = logger;
    }

    /// <summary>
    /// 处理一个 type=event 帧。同步返回（不阻塞 ReceiveLoop），实际转发在后台 Task.Run 中执行。
    ///
    /// F-LIFETIME 关键约束：上游 HandleMessageAsync 用 using var doc = JsonDocument.Parse(json)，
    /// 本方法返回后 doc 立即 Dispose、frame 立即指向已释放对象。
    /// 因此本方法的同步阶段必须：
    ///   (a) 把 frame 重写为 owned 字符串（forwarded JSON）
    ///   (b) 把订阅者 clientId 列表 snapshot 到数组
    ///   (c) voice 桥接也必须在同步阶段完成（Bridge 内部读 JsonElement）
    /// 后台 Task.Run 内只用 owned 字符串和 clientId 数组，绝不再读 JsonElement 或 _subscriptions 字典。
    /// </summary>
    public Task HandleAsync(string sourceClientId, JsonElement frame, CancellationToken ct)
    {
        // R-PAYLOAD-AMP：单帧字节上限二次防御（ReadTextMessageAsync 16K 是读缓冲不是消息上限）
        var rawLength = frame.GetRawText().Length;
        if (rawLength > MaxFrameBytes)
        {
            _logger.LogWarning(
                "Event dropped: frame too large. Bytes={Bytes} Limit={Limit} ClientId={ClientId}",
                rawLength, MaxFrameBytes, sourceClientId);
            return Task.CompletedTask;
        }

        // F3：host source 排除（v1 仅信任 clientId 排除自回环，但 source==host 的客户端冒充立即拒）
        var source = ReadString(frame, "source");
        if (string.Equals(source, "host", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Client uploaded host-source event, ignored. ClientId={ClientId}", sourceClientId);
            return Task.CompletedTask;
        }

        // F2 / F-PRIORITY：op > eventType > type 优先级（1.4.0 有意变更，旧版为 eventType > op > type）
        if (!TryParse(frame, out var op, out var hops, out var sourcePluginId))
        {
            _logger.LogWarning(
                "Event frame missing op/eventType. ClientId={ClientId}", sourceClientId);
            return Task.CompletedTask;
        }

        // R-CHAT-MISFIRE：受保护 op 黑名单守卫，命中即拒收，不进入路由
        if (IsProtectedOp(op))
        {
            _logger.LogWarning(
                "Event dropped: protected op cannot be broadcast. Op={Op} ClientId={ClientId}",
                op, sourceClientId);
            return Task.CompletedTask;
        }

        if (hops >= MaxHopCount)
        {
            _logger.LogWarning(
                "Event dropped: hop limit exceeded. Op={Op} Hops={Hops} ClientId={ClientId}",
                op, hops, sourceClientId);
            return Task.CompletedTask;
        }

        // F-SOURCE：弱声明校验从 frame 读 sourcePluginId（缺省 fallback 到 source），仅 LogWarning
        ValidatePublisherDeclaration(sourcePluginId, op);

        var topic = ExtractNamespace(op);

        // F-LIFETIME 同步阶段 (a)：重写 hopCount 得到 owned JSON 字符串，脱离 JsonElement 生命周期
        var forwardedJson = RewriteHopCount(frame, hops + 1);

        // F-LIFETIME 同步阶段 (b)：snapshot 订阅者 clientId 列表为数组，脱离 _subscriptions 字典
        var targetClientIds = _subscriptions.GetSubscribers(topic)
            .Where(cid => !string.Equals(cid, sourceClientId, StringComparison.Ordinal))
            .Where(cid => _subscriptions.MatchesSubscribedOp(cid, op))
            .ToArray();

        // F-LIFETIME 同步阶段 (c)：voice 桥接必须在 frame 仍然有效时调用
        if (string.Equals(topic, "voice", StringComparison.Ordinal))
        {
            _voiceBridge.Bridge(op, frame);
        }

        // F6：后台投递，HandleAsync 立刻返回；后台只用 owned 数据
        _ = Task.Run(
            () => ForwardToSubscribersAsync(targetClientIds, op, forwardedJson, ct),
            ct);

        return Task.CompletedTask;
    }

    private static bool TryParse(
        JsonElement frame,
        out string op,
        out int hops,
        out string sourcePluginId)
    {
        // F2 / F-PRIORITY：op > eventType > type 优先级
        // 1.4.0 有意变更：op 与 eventType 同时存在且不一致时以 op 为权威；
        // 老插件无 op 时 fallback 到 eventType，不破坏 1.3.0 行为。
        op = ReadString(frame, "op")
            ?? ReadString(frame, "eventType")
            ?? ReadString(frame, "type")
            ?? string.Empty;

        sourcePluginId = ReadString(frame, "sourcePluginId")
            ?? ReadString(frame, "source")
            ?? string.Empty;

        hops = frame.TryGetProperty("hopCount", out var h) && h.ValueKind == JsonValueKind.Number
            ? h.GetInt32()
            : 0;

        // type 字段如果是控制字（"event" / "subscribe" / "pong" 等），不能被当 op 误用
        if (string.Equals(op, "event", StringComparison.Ordinal)
            || string.Equals(op, "subscribe", StringComparison.Ordinal)
            || string.Equals(op, "pong", StringComparison.Ordinal)
            || string.Equals(op, "send", StringComparison.Ordinal)
            || string.Equals(op, "stop", StringComparison.Ordinal))
        {
            op = string.Empty;
        }

        return !string.IsNullOrWhiteSpace(op);
    }

    private static string ExtractNamespace(string op)
    {
        var dot = op.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? op[..dot] : op;
    }

    private void ValidatePublisherDeclaration(string sourcePluginId, string op)
    {
        // v1 弱声明，仅 LogWarning，不强拒（与 5B-D 一致）；不作为安全依据
        if (string.IsNullOrWhiteSpace(sourcePluginId)) return;
        if (_manifests.HasPublishedOp(sourcePluginId, op)) return;

        _logger.LogWarning(
            "Event publisher not declared in plugin.json. PluginId={PluginId} Op={Op}",
            sourcePluginId, op);
    }

    private static string RewriteHopCount(JsonElement frame, int newHops)
    {
        // 复制原帧、重写 hopCount，避免修改入参
        // F-LIFETIME：返回 owned UTF-8 字符串，脱离 frame 的 JsonDocument 生命周期
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var hopWritten = false;
            foreach (var property in frame.EnumerateObject())
            {
                if (string.Equals(property.Name, "hopCount", StringComparison.Ordinal))
                {
                    writer.WriteNumber("hopCount", newHops);
                    hopWritten = true;
                    continue;
                }
                property.WriteTo(writer);
            }
            if (!hopWritten)
            {
                writer.WriteNumber("hopCount", newHops);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task ForwardToSubscribersAsync(
        IReadOnlyCollection<string> targetClientIds,
        string op,
        string forwardedJson,
        CancellationToken ct)
    {
        // F-LIFETIME：本方法只接收 owned 数据（clientId 数组 + JSON 字符串），
        // 不再访问 _subscriptions 字典或任何 JsonElement，可安全地在后台 Task.Run 内执行
        // F6：per-subscriber 1 秒超时，慢订阅者不阻塞其他人
        var tasks = targetClientIds.Select(cid => SendWithTimeoutAsync(cid, op, forwardedJson, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendWithTimeoutAsync(string cid, string op, string payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerSubscriberTimeoutMs);
        try
        {
            await _sendAsync(cid, payload, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // R-DEAD-SUBSCRIBER：1 秒 linked CTS cancel 会击穿
            // WebSocketConnectionManager.SendAsync 的 `when (!cancellationToken.IsCancellationRequested)` 守卫，
            // 自动 RemoveAndCloseAsync 不触发，必须主动清理；否则死链残留导致每次广播都重新尝试 1 秒超时
            _logger.LogWarning("Forward timeout, dead-link cleanup. Target={Cid} Op={Op}", cid, op);
            _subscriptions.Remove(cid);
            await _pluginBusConnections.RemoveAndCloseAsync(cid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Forward failed. Target={Cid} Op={Op}", cid, op);
        }
    }

    /// <summary>
    /// R-CHAT-MISFIRE 受保护 op 黑名单守卫。
    /// 命中即拒收，不进入广播路由——避免客户端 SDK bug 把 RPC / 控制帧误标 type=event 时
    /// 走广播路径错投给订阅者，进而打断对话 / 记忆 / 模型链路。
    /// 列表 = 现有 RPC / 控制 op 全集（OQ3 重命名后 chat.* 都在 conversation 子命名空间下）。
    /// </summary>
    private static bool IsProtectedOp(string op)
    {
        return op switch
        {
            "conversation.chat.message.send" => true,
            "conversation.chat.generation.stop" => true,
            "system.notice" => true,
            "conversation.history.replay" => true,
            "workflow.history.replay" => true,
            "meeting.history.replay" => true,
            "replay" => true,
            "model.capability.request" => true,
            "model.capability.response" => true,
            "memory.context.supply.request" => true,
            "memory.context.supply.response" => true,
            "memory.context.supply.error" => true,
            _ => false,
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }
}
