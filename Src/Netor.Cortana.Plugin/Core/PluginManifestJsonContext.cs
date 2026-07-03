using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(JsonStringEnumConverter<PluginRuntime>)])]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(RequiredHostCapability))]
[JsonSerializable(typeof(PluginSettingDescriptor))]
[JsonSerializable(typeof(PublishedOpDeclaration))]
[JsonSerializable(typeof(SubscribedOpDeclaration))]
public partial class PluginManifestJsonContext : JsonSerializerContext;
