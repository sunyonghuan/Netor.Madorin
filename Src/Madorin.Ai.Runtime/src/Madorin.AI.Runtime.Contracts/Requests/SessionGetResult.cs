using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionGetResult(
    string SessionId,
    RuntimeMode Mode,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    NextTurnSelection? Selection,
    RunQueryResult? LatestRun,
    bool IsFullyRecoverable,
    string[] RecoveryDiagnostics);
