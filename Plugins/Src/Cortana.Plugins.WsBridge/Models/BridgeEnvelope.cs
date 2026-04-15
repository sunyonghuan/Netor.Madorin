using System.Text.Json.Serialization;

namespace Cortana.Plugins.WsBridge.Models;

/// <summary>
/// 统一消息信封，所有双向消息都通过此结构中转。
/// </summary>
public sealed record BridgeEnvelope
{
    [JsonPropertyName("message_id")] public string MessageId { get; init; } = string.Empty;
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("target")] public string Target { get; init; } = string.Empty;
    [JsonPropertyName("event_type")] public string EventType { get; init; } = string.Empty;
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("payload")] public string? Payload { get; init; }
}
