namespace Madorin.AI.Runtime.Persistence.Sqlite;

public sealed record MeetingParticipantInput(
    string ParticipantId,
    string AgentId,
    string? AgentRefJson,
    string? DisplayName,
    int JoinOrder,
    string Status = "active");

public sealed record MeetingSessionRecord(
    string SessionId,
    string RunId,
    string Status,
    int CurrentRound,
    byte[]? SelectorState,
    string PolicyJson,
    string PolicyHash,
    int SelectionVersion,
    string? PendingApprovalRequestId,
    string? PendingApprovalJson,
    string? PendingApprovalStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MeetingRoundRecord(
    string SessionId,
    int RoundIndex,
    string RunId,
    string Status,
    string? FirstInvocationId,
    string? SummaryMessageId,
    long? SummarizesThroughSeq,
    string? SummaryPolicyHash,
    string? SummaryAgentId,
    string? SummaryInvocationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record MeetingParticipantRecord(
    string SessionId,
    string ParticipantId,
    string AgentId,
    string? AgentRefJson,
    string? DisplayName,
    int JoinOrder,
    string Status,
    int JoinedSelectionVersion,
    int? RemovedSelectionVersion,
    DateTimeOffset JoinedAt,
    DateTimeOffset? RemovedAt,
    DateTimeOffset UpdatedAt);

public sealed record MeetingInvocationRecord(
    string InvocationId,
    string SessionId,
    string RunId,
    int RoundIndex,
    int Ordinal,
    string? ParticipantId,
    string? AgentId,
    string? Role,
    string Status,
    int SelectionVersion,
    int RetryCount,
    string? MessageId,
    string? SelectorDecisionJson,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record MeetingInvocationScheduleInput(
    string InvocationId,
    string? ParticipantId,
    string? AgentId,
    string? Role,
    int SelectionVersion);

public sealed record MeetingSnapshot(
    MeetingSessionRecord Session,
    IReadOnlyList<MeetingParticipantRecord> Participants,
    MeetingRoundRecord? CurrentRound,
    MeetingInvocationRecord? NextScheduledInvocation,
    string? PendingApprovalJson,
    string? PendingApprovalStatus,
    MeetingRoundRecord? LatestSummary);

public sealed record MeetingInvocationRecoveryItem(
    string InvocationId,
    string SessionId,
    string RunId,
    int RoundIndex,
    int Ordinal,
    string Status,
    string? ParticipantId,
    string? AgentId,
    string? Role = null,
    int SelectionVersion = 0,
    int RetryCount = 0);

public sealed record MeetingInvocationReconciliationItem(
    string InvocationId,
    string SessionId,
    string RunId,
    int RoundIndex,
    int Ordinal,
    string Status,
    string? MessageId,
    string? AgentId);

public sealed record MeetingApprovalRecord(
    string SessionId,
    string RunId,
    string ApprovalRequestId,
    string? ApprovalJson,
    string? Status);

public sealed record MeetingInvocationContextRecord(
    string InvocationId,
    int RoundIndex,
    string? Role);
