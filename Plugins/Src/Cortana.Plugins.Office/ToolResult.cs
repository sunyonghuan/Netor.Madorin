using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office;

/// <summary>
/// 工具统一返回结构。所有工具方法最终返回此类型序列化后的 JSON 字符串。
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }

    /// <summary>生成成功响应。</summary>
    public static string Ok(string message, string? data = null) =>
        Serialize(new ToolResult { Success = true, Code = ErrorCodes.Ok, Message = message, Data = data });

    /// <summary>生成失败响应。</summary>
    public static string Fail(string code, string message, string? data = null) =>
        Serialize(new ToolResult { Success = false, Code = code, Message = message, Data = data });

    private static string Serialize(ToolResult result) =>
        JsonSerializer.Serialize(result, PluginJsonContext.Default.ToolResult);
}
