using System.Text.Json.Serialization;

namespace Netor.Cortana.UI;

/// <summary>
/// AOT 兼容的 JSON 序列化上下文，为 AppSettings 及其子类型提供源生成器支持。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AliyunSettings))]
[JsonSerializable(typeof(SherpaOnnxSettings))]
[JsonSerializable(typeof(TtsSettings))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
