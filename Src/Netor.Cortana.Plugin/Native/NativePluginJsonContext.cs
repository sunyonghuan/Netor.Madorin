using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.Native;

/// <summary>
/// Native 插件 JSON 源生成器上下文（AOT 兼容）。
/// </summary>
[JsonSerializable(typeof(NativeHostRequest))]
[JsonSerializable(typeof(NativeHostResponse))]
[JsonSerializable(typeof(NativePluginInfo))]
[JsonSerializable(typeof(NativePluginInitExtensions))]
[JsonSerializable(typeof(NativePluginInitConfig))]
internal partial class NativePluginJsonContext : JsonSerializerContext;

/// <summary>
/// 插件 init 扩展槽位。
/// </summary>
public sealed class NativePluginInitExtensions : Dictionary<string, string>
{
    /// <summary>创建插件 init 扩展槽位。</summary>
    public NativePluginInitExtensions() : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>
/// 原生插件初始化配置（替代匿名类型，AOT 兼容）。
/// </summary>
public sealed record NativePluginInitConfig
{
    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; init; } = string.Empty;

    [JsonPropertyName("workspaceDirectory")]
    public string WorkspaceDirectory { get; init; } = string.Empty;

    [JsonPropertyName("wsPort")]
    public int WsPort { get; init; }


    [JsonPropertyName("pluginDirectory")]
     public string PluginDirectory {  get; init;}= string.Empty;

    [JsonPropertyName("extensions")]
    public NativePluginInitExtensions? Extensions { get; init; }
}
