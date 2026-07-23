using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Cli.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StandaloneConfig))]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(RuntimeInstanceInfo))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
