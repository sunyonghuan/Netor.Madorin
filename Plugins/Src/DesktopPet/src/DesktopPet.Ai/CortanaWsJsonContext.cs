using System.Text.Json.Serialization;

namespace DesktopPet.Ai;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CortanaWsAttachment))]
[JsonSerializable(typeof(CortanaWsClientMessage))]
[JsonSerializable(typeof(CortanaWsServerMessage))]
[JsonSerializable(typeof(IReadOnlyList<CortanaWsAttachment>))]
public sealed partial class CortanaWsJsonContext : JsonSerializerContext
{
}
