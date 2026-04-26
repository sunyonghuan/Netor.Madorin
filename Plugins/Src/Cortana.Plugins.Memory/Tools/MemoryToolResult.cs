using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortana.Plugins.Memory.Tools;

/// <summary>
/// 记忆工具统一返回结构。
/// </summary>
public sealed record MemoryToolResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }

    /// <summary>生成成功响应。</summary>
    public static string Ok(string message, string? data = null) =>
        Serialize(new MemoryToolResult { Success = true, Code = MemoryToolErrorCodes.Ok, Message = message, Data = data });

    /// <summary>生成失败响应。</summary>
    public static string Fail(string code, string message, string? data = null) =>
        Serialize(new MemoryToolResult { Success = false, Code = code, Message = message, Data = data });

    private static string Serialize(MemoryToolResult result) =>
        JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryToolResult);
}
