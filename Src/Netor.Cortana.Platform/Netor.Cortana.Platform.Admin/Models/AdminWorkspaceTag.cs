namespace Netor.Cortana.Platform.Admin.Models;

public sealed record AdminWorkspaceTag(
    string Label,
    string Value,
    string? Href = null,
    string? IconClass = null);
