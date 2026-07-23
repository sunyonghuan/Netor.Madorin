namespace Madorin.AI.Runtime.Entities;

public sealed record PersistedSessionSnapshot(
    string SessionId,
    RuntimeMode Mode,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? SelectionVersion,
    string? SelectionJson,
    RunSnapshot? LatestRun,
    AgentSnapshot[] AgentSnapshots,
    long LastGsn);
