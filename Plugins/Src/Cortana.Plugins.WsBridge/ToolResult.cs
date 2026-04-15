using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortana.Plugins.WsBridge;

/// <summary>
/// 插件工具统一返回结构。
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }

    public static string Ok(string message, string? data = null) => Serialize(new ToolResult
    {
        Success = true, Code = "OK", Message = message, Data = data
    });

    public static string Fail(string code, string message, string? data = null) => Serialize(new ToolResult
    {
        Success = false, Code = code, Message = message, Data = data
    });

    private static string Serialize(ToolResult result) =>
        JsonSerializer.Serialize(result, PluginJsonContext.Default.ToolResult);
}
