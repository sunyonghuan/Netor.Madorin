using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortana.Plugins.GoogleSearch;

/// <summary>
/// 插件工具统一返回结构。
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("data")] public object? Data { get; init; }

    /// <summary>
    /// 生成成功响应。
    /// </summary>
    public static ToolResult Ok(string message, object? data = null) => new()
    {
        Success = true,
        Code = "OK",
        Message = message,
        Data = data
    };

    /// <summary>
    /// 生成失败响应。
    /// </summary>
    public static ToolResult Fail(string code, string message, object? data = null) => new()
    {
        Success = false,
        Code = code,
        Message = message,
        Data = data
    };
}