using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Madorin.Plugin;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters =
    [
        typeof(JsonStringEnumConverter<PluginRuntime>),
        typeof(JsonStringEnumConverter<ToolRiskLevel>)
    ])]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(RequiredHostCapability))]
[JsonSerializable(typeof(PluginSettingDescriptor))]
[JsonSerializable(typeof(PluginToolDescriptor))]
[JsonSerializable(typeof(PublishedOpDeclaration))]
[JsonSerializable(typeof(SubscribedOpDeclaration))]
public partial class PluginManifestJsonContext : JsonSerializerContext;
