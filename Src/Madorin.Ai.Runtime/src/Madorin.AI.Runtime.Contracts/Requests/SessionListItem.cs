using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionListItem(
    string SessionId,
    RuntimeMode Mode,
    SessionStatus Status,
    DateTimeOffset UpdatedAt,
    string? Title);
