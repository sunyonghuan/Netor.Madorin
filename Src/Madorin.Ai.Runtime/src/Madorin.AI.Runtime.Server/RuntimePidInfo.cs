using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// Written to <c>.madorin/runtime.pid</c> while the server is running
/// so that <c>madorin run</c> can discover the Named Pipe endpoint.
/// </summary>
public sealed record RuntimePidInfo(
    string InstanceId,
    string PipeName,
    string EventPipeName,
    int Pid,
    DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimePidInfo))]
public sealed partial class RuntimePidInfoContext : JsonSerializerContext;
