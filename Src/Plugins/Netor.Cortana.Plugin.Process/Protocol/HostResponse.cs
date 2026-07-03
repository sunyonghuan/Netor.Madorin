using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.Process.Protocol;

/// <summary>
/// 插件子进程返回给宿主的单行 JSON 响应。
/// <para>
/// 通过 stdout 写出，每行一个响应。<see cref="Success"/> 为 <c>false</c> 时
/// <see cref="Error"/> 必须包含人类可读的错误信息；
/// 为 <c>true</c> 时 <see cref="Data"/> 通常是工具返回值或序列化后的元数据。
/// </para>
/// </summary>
public sealed record HostResponse
{
    /// <summary>调用是否成功。</summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>成功时的数据（工具返回字符串或 <see cref="PluginInfoData"/> 的 JSON）。</summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>失败时的错误信息。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>构造成功响应。</summary>
    public static HostResponse Ok(string? data) => new() { Success = true, Data = data };

    /// <summary>构造失败响应。</summary>
    public static HostResponse Fail(string error) => new() { Success = false, Error = error };
}
