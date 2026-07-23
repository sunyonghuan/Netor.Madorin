using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public sealed record NetworkPolicy(
    bool DenyAll = true,
    string[] AllowedHosts = null!,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AllowedSchemes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int[]? AllowedPorts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AllowedAddressRanges = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxRedirects = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? MaxResponseBytes = null);
