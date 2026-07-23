using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeCapabilities(
    bool Streaming = true,
    bool ToolCalling = true,
    bool BlobTransfer = true,
    bool MultiRun = true,
    bool SessionResume = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ReverseRpc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ToolCatalog = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ToolPermissions = null);
