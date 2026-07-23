using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeLimits(
    int MaxControlMessageBytes = 1048576,
    int MaxEventFrameBytes = 4194304,
    int MaxInlineContentBytes = 65536,
    long MaxBlobBytes = 1073741824L,
    long MaxBlobBytesPerRun = 4294967296L,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxToolRounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxToolCallsPerRound = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? MaxToolResultBytesPerInvocation = null);
