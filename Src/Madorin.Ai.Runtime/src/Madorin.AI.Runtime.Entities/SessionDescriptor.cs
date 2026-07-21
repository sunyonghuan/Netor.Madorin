namespace Madorin.AI.Runtime.Entities;

public sealed record SessionDescriptor(
    string SessionId,
    RuntimeMode Mode,
    SessionStatus Status,
    DateTimeOffset UpdatedAt,
    string? Title);
