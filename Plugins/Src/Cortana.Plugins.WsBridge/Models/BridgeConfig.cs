using System.Text.Json.Serialization;

namespace Cortana.Plugins.WsBridge.Models;

/// <summary>
/// 中转桥接配置，描述一个外部应用的连接参数。
/// </summary>
public sealed record BridgeConfig
{
    [JsonPropertyName("adapter_id")] public string AdapterId { get; init; } = string.Empty;
    [JsonPropertyName("ws_url")] public string WsUrl { get; init; } = string.Empty;
    [JsonPropertyName("auth_token")] public string? AuthToken { get; init; }
}
