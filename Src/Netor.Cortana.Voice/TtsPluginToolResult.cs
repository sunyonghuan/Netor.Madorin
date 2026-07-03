using System.Text.Json.Serialization;

namespace Netor.Cortana.Voice;

/// <summary>
/// 表示 TTS 插件工具调用的宿主侧解析结果。
/// </summary>
public sealed record TtsPluginToolResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message)
{
    public static TtsPluginToolResult Skipped(string message)
        => new(true, "skipped", message);

    public static TtsPluginToolResult Failed(string code, string message)
        => new(false, code, message);
}
