using System.Text.Json.Serialization;

namespace Cortana.Plugins.WsBridge.Models;

/// <summary>
/// 客户端发往 Cortana WebSocket 的消息（send / stop）。
/// </summary>
public sealed record CortanaClientMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }
}

/// <summary>
/// Cortana WebSocket 服务端下发的消息（connected / token / done / error 等）。
/// </summary>
public sealed record CortanaServerMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}
