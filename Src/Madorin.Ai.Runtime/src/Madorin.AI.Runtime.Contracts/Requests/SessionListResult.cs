namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionListResult(
    SessionListItem[] Sessions,
    string? NextCursor);
