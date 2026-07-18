using System.Text.Json;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// 统一创建 PluginBus 协议消息，避免各分发器重复拼装协议字段。
/// </summary>
internal static class PluginBusMessageFactory
{
    /// <summary>
    /// 创建聊天事件消息 JSON。
    /// </summary>
    public static string CreateChatEvent(string operation, string? target, WsMessage message)
    {
        return JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "event",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.ConversationTopic,
            Op = operation,
            Source = "host",
            Target = target,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = operation,
            Payload = JsonSerializer.SerializeToElement(message, WebSocketJsonContext.Default.WsMessage)
        }, WebSocketJsonContext.Default.PluginBusEventMessage);
    }

    /// <summary>
    /// 创建 PluginBus 控制错误消息 JSON。
    /// </summary>
    public static string CreateControlError(string clientId, string message)
    {
        return JsonSerializer.Serialize(new PluginBusControlMessage
        {
            Type = "error",
            ClientId = clientId,
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Message = message
        }, WebSocketJsonContext.Default.PluginBusControlMessage);
    }

    /// <summary>
    /// 创建连接成功控制消息 JSON。
    /// </summary>
    public static string CreateConnected(string clientId)
    {
        return JsonSerializer.Serialize(new PluginBusControlMessage
        {
            Type = "connected",
            ClientId = clientId,
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topics =
            [
                CortanaWsEndpoints.ConversationTopic,
                CortanaWsEndpoints.MemoryTopic,
                CortanaWsEndpoints.ModelTopic,
                CortanaWsEndpoints.PluginTopic,
                CortanaWsEndpoints.WorkflowTopic,
                CortanaWsEndpoints.MeetingTopic,
                CortanaWsEndpoints.WorkspaceTopic
            ]
        }, WebSocketJsonContext.Default.PluginBusControlMessage);
    }

    /// <summary>
    /// 创建订阅确认控制消息 JSON。
    /// </summary>
    public static string CreateSubscribed(string clientId, string[] topics)
    {
        return JsonSerializer.Serialize(new PluginBusControlMessage
        {
            Type = "subscribed",
            ClientId = clientId,
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topics = topics
        }, WebSocketJsonContext.Default.PluginBusControlMessage);
    }
}
