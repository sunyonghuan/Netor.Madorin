using Netor.Cortana.Entitys;

using System.Text.Json;

namespace Netor.Cortana.Networks;

/// <summary>
/// 通过 <see cref="IChatTransport"/> 将宿主聊天输入输出适配为 PluginBus conversation topic 消息。
/// </summary>
internal sealed class PluginBusChatDispatcher(
    WebSocketConnectionManager connections,
    PluginBusSubscriptionRegistry subscriptions)
{
    /// <summary>
    /// 将 AI 生成的增量 token 发送给指定客户端。
    /// </summary>
    public Task SendTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
    {
        var payload = PluginBusMessageFactory.CreateChatEvent("conversation.chat.token", clientId, new WsMessage { Type = "token", Data = token });
        return connections.SendAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 将 AI 生成完成事件发送给指定客户端。
    /// </summary>
    public Task SendDoneAsync(string clientId, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var payload = PluginBusMessageFactory.CreateChatEvent("conversation.chat.done", clientId, new WsMessage { Type = "done", SessionId = sessionId });
        return connections.SendAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 将 AI 生成错误事件发送给指定客户端。
    /// </summary>
    public Task SendErrorAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        var payload = PluginBusMessageFactory.CreateChatEvent("conversation.chat.error", clientId, new WsMessage { Type = "error", Data = message });
        return connections.SendAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 向订阅 conversation topic 的客户端广播宿主事件。
    /// </summary>
    public async Task BroadcastAsync(string type, string data, CancellationToken cancellationToken = default)
    {
        var payload = PluginBusMessageFactory.CreateChatEvent($"conversation.chat.{type}", "client", new WsMessage { Type = type, Data = data });
        var tasks = subscriptions.GetSubscribers(CortanaWsEndpoints.ConversationTopic)
            .Select(clientId => connections.SendAsync(clientId, payload, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 将 PluginBus chat payload 转为旧聊天传输事件所需的参数。
    /// </summary>
    public static PluginBusChatMessage ReadMessage(string clientId, JsonElement root, string fallbackType)
    {
        var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
            ? payloadElement
            : root;
        var type = payload.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? fallbackType : fallbackType;
        var data = payload.TryGetProperty("data", out var dataElement) ? dataElement.GetString() ?? string.Empty : string.Empty;
        var title = payload.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var level = payload.TryGetProperty("level", out var levelElement) ? levelElement.GetString() : null;
        var source = payload.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() : null;
        var attachments = ReadAttachments(payload);

        return new PluginBusChatMessage(clientId, type, data, attachments, title, level, source);
    }

    /// <summary>
    /// 从聊天消息载荷中读取附件列表。
    /// </summary>
    private static List<AttachmentInfo> ReadAttachments(JsonElement payload)
    {
        var attachments = new List<AttachmentInfo>();
        if (!payload.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var path = item.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty;
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var mimeType = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
            {
                attachments.Add(new AttachmentInfo(path, name, mimeType));
            }
        }

        return attachments;
    }
}

/// <summary>
/// PluginBus 聊天输入消息。
/// </summary>
internal sealed record PluginBusChatMessage(
    string ClientId,
    string Type,
    string Data,
    List<AttachmentInfo> Attachments,
    string? Title,
    string? Level,
    string? Source);
