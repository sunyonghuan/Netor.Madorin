using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimeError))]
[JsonSerializable(typeof(RuntimeEventEnvelope))]
[JsonSerializable(typeof(RuntimeVersionInfo))]
public sealed partial class RuntimeJsonContext : JsonSerializerContext;
