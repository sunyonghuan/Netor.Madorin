using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.Native;
/// 通过 stdin 以单行 JSON 发送。
/// </summary>
public sealed record NativeHostRequest
{
    /// <summary>
    /// 请求方法：get_info / init / invoke / destroy。
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    /// <summary>
    /// 工具名称（仅 invoke 时使用）。
    /// </summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    /// <summary>
    /// 参数 JSON（init 传配置，invoke 传工具参数）。
    /// </summary>
    [JsonPropertyName("args")]
    public string? Args { get; init; }
}

/// <summary>
/// NativeHost 子进程 → 宿主的响应消息。
/// 通过 stdout 以单行 JSON 返回。
/// </summary>
public sealed record NativeHostResponse
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// 成功时返回的数据（JSON 字符串或纯文本）。
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>
    /// 失败时的错误信息。
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// 协议常量。
/// </summary>
public static class NativeHostMethods
{
    /// <summary>获取插件信息和工具列表。</summary>
    public const string GetInfo = "get_info";

    /// <summary>初始化插件（传入配置 JSON）。</summary>
    public const string Init = "init";

    /// <summary>调用工具。</summary>
    public const string Invoke = "invoke";

    /// <summary>销毁插件并释放资源。</summary>
    public const string Destroy = "destroy";
}
