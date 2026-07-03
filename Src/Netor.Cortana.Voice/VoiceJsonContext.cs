using System.Text.Json.Serialization;

namespace Netor.Cortana.Voice;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TtsPluginToolResult))]
[JsonSerializable(typeof(VoicePluginToolResult))]
internal sealed partial class VoiceJsonContext : JsonSerializerContext;
