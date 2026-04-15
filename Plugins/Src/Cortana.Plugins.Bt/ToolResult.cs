using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 插件工具统一返回结构。
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }

    /// <summary>
    /// 生成成功响应。
    /// </summary>
    public static string Ok(string message, string? data = null) => Serialize(new ToolResult
    {
        Success = true,
        Code = "OK",
        Message = message,
        Data = data
    });

    /// <summary>
    /// 生成失败响应。
    /// </summary>
    public static string Fail(string code, string message, string? data = null) => Serialize(new ToolResult
    {
        Success = false,
        Code = code,
        Message = message,
        Data = data
    });

    /// <summary>
    /// 使用源码生成的 Json 上下文序列化结果，保持 AOT 兼容。
    /// </summary>
    private static string Serialize(ToolResult result) => JsonSerializer.Serialize(result, PluginJsonContext.Default.ToolResult);
}
