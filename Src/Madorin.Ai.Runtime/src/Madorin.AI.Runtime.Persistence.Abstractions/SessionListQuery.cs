using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Parameters for listing sessions with optional filters.</summary>
public sealed record SessionListQuery(
    RuntimeMode? Mode = null,
    SessionStatus? Status = SessionStatus.Active,
    DateTimeOffset? Since = null,
    string? Search = null,
    int Limit = 20,
    string? Cursor = null);
