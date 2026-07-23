namespace Madorin.AI.Runtime.Contracts;

/// <summary>Durable meeting state returned by <c>session.resume</c>.</summary>
public sealed record MeetingResumeState(
    MeetingSessionStatus Status,
    int CurrentRound,
    MeetingParticipant[] Participants,
    int SelectionVersion,
    MeetingScheduledInvocation? NextScheduledInvocation,
    MeetingHitlRequest? PendingApproval,
    MeetingSummary? RecentSummary);

/// <summary>Identifies a scheduled or executed participant invocation within a meeting.</summary>
public sealed record MeetingScheduledInvocation(
    string InvocationId,
    int RoundIndex,
    MeetingInvocationRole Role,
    string? ParticipantId,
    string AgentId,
    int SelectionVersion,
    MeetingInvocationStatus Status);

/// <summary>A summary generated for a completed round or the entire meeting.</summary>
public sealed record MeetingSummary(
    string SummaryMessageId,
    int? RoundIndex,
    long SummarizesThroughSeq,
    string PolicyHash,
    string SummarizerAgentId,
    string SummarizerInvocationId);
