using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionResumeResult(
    string SessionId,
    RuntimeMode Mode,
    int? LatestSelectionVersion,
    NextTurnSelection? Selection,
    NeededAgentDefinition[] NeededDefinitions,
    RunQueryResult? LatestRun,
    long LastGsn,
    string? LastCheckpoint,
    bool IsFullyRecoverable,
    string[] RecoveryDiagnostics,
    long MessageCount = 0,
    long? LastMessageSeq = null,
    string? LastMessageId = null,
    AgentSnapshot[]? AgentSnapshots = null,
    int BlobReferenceCount = 0,
    string[]? BlobIds = null,
    MeetingResumeState? MeetingState = null,
    WorkResumeState? WorkState = null);