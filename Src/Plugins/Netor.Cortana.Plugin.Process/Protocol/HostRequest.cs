using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.Process.Protocol;

/// <summary>
/// 宿主发送给插件子进程的单行 JSON 请求。
/// <para>
/// 通过 stdin 传入，每行一个请求。字段 <c>method</c> 必填，
/// 其余字段根据方法不同而不同：
/// <list type="bullet">
///   <item><c>get_info</c>：无额外字段</item>
///   <item><c>init</c>：<c>args</c> 为 <see cref="Settings.InitConfig"/> 的 JSON 字符串</item>
///   <item><c>invoke</c>：<c>toolName</c> 工具名，<c>args</c> 工具参数 JSON 字符串</item>
///   <item><c>destroy</c>：无额外字段</item>
/// </list>
/// </para>
/// </summary>
public sealed record HostRequest
{
    /// <summary>方法名：<c>get_info</c> / <c>init</c> / <c>invoke</c> / <c>destroy</c>。</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    /// <summary>工具名（仅 <c>invoke</c> 方法有效）。</summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    /// <summary>参数 JSON 字符串（<c>init</c> 和 <c>invoke</c> 方法使用）。</summary>
    [JsonPropertyName("args")]
    public string? Args { get; init; }
}
