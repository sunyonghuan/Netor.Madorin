using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionListParameters(
    RuntimeMode? Mode = null,
    SessionStatus? Status = SessionStatus.Active,
    DateTimeOffset? Since = null,
    string? Search = null,
    int Limit = 20,
    string? Cursor = null);
