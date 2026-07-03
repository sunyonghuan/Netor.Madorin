using System.Text.Json.Serialization;

namespace Netor.Cortana.Voice;

/// <summary>
/// 表示语音插件工具调用的宿主侧解析结果。
/// </summary>
public sealed record VoicePluginToolResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message)
{
    public static VoicePluginToolResult Skipped(string message)
        => new(true, "skipped", message);

    public static VoicePluginToolResult Failed(string code, string message)
        => new(false, code, message);
}
