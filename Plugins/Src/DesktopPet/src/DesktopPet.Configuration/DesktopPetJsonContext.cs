using DesktopPet.Abstractions;
using System.Text.Json.Serialization;

namespace DesktopPet.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DesktopPetSettings))]
[JsonSerializable(typeof(PetWindowPlacement))]
public sealed partial class DesktopPetJsonContext : JsonSerializerContext
{
}
