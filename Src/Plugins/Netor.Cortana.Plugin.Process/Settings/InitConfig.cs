using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.Process.Settings;

/// <summary>
/// <c>init</c> 请求的 <c>args</c> 字段内容。
/// 宿主在插件启动后通过此结构注入目录和端口信息。
/// </summary>
public sealed record InitExtensions
{
    [JsonPropertyName("chatWsEndpoint")]
    public string ChatWsEndpoint { get; init; } = string.Empty;

    [JsonPropertyName("conversationFeedEndpoint")]
    public string ConversationFeedEndpoint { get; init; } = string.Empty;

    [JsonPropertyName("conversationFeedProtocol")]
    public string ConversationFeedProtocol { get; init; } = string.Empty;

    [JsonPropertyName("conversationFeedVersion")]
    public string ConversationFeedVersion { get; init; } = string.Empty;
}

public sealed record InitConfig
{
    /// <summary>插件专属数据目录。</summary>
    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; init; } = string.Empty;

    /// <summary>当前工作区目录。</summary>
    [JsonPropertyName("workspaceDirectory")]
    public string WorkspaceDirectory { get; init; } = string.Empty;

    /// <summary>插件目录（插件包根目录）。</summary>
    [JsonPropertyName("pluginDirectory")]
    public string PluginDirectory { get; init; } = string.Empty;

    /// <summary>宿主 WebSocket 端口。</summary>
    [JsonPropertyName("wsPort")]
    public int WsPort { get; init; }

    /// <summary>插件扩展槽位。</summary>
    [JsonPropertyName("extensions")]
    public InitExtensions? Extensions { get; init; }
}
