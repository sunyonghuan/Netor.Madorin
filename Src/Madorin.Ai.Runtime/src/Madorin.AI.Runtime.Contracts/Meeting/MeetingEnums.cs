namespace Madorin.AI.Runtime.Contracts;

/// <summary>Lifecycle status of a meeting participant.</summary>
public enum ParticipantStatus
{
    Active,
    Standby,
    Removed
}

/// <summary>How the meeting selects the next participant to invoke.</summary>
public enum MeetingSelectorPolicy
{
    RoundRobin,
    SelectorDriven,
    HostDriven
}

/// <summary>When summaries are generated during a meeting.</summary>
public enum MeetingSummaryMode
{
    None,
    PerRound,
    FinalOnly
}

/// <summary>How the canonical history is projected for meeting roles.</summary>
public enum MeetingContextStrategy
{
    Full,
    TailWindow,
    SlidingWindow,
    SummaryCompressed
}

/// <summary>Overall status of a meeting session.</summary>
public enum MeetingSessionStatus
{
    NotStarted,
    Running,
    WaitingForApproval,
    Paused,
    Summarizing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Status of a scheduled or executed meeting invocation.</summary>
public enum MeetingInvocationStatus
{
    Scheduled,
    Running,
    Completed,
    Skipped,
    Interrupted,
    Failed
}

/// <summary>User decision for a meeting HITL request.</summary>
public enum MeetingHitlAction
{
    Approve,
    Reject,
    Supplement,
    Cancel
}

/// <summary>How the meeting reacts when a participant invocation fails.</summary>
public enum MeetingParticipantFailurePolicy
{
    Skip,
    Retry,
    Pause,
    Fail
}

/// <summary>How the meeting reacts when the host invocation fails.</summary>
public enum MeetingHostFailurePolicy
{
    Pause,
    Retry,
    Fail
}

/// <summary>How the meeting reacts when the selector invocation fails.</summary>
public enum MeetingSelectorFailurePolicy
{
    DeterministicFallback,
    RetryThenFallback,
    Pause,
    Fail
}

/// <summary>How the meeting reacts when the summarizer invocation fails.</summary>
public enum MeetingSummarizerFailurePolicy
{
    FallbackTailWindow,
    FallbackFull,
    RetryThenTailWindow,
    Fail
}

/// <summary>Condition that authorizes the meeting to conclude.</summary>
public enum MeetingConclusionCondition
{
    MaxRounds,
    HostDecides,
    ConsensusReached
}

/// <summary>Fallback projection used when summary compression cannot complete.</summary>
public enum MeetingSummaryFallbackStrategy
{
    TailWindow,
    Full,
    Fail
}

/// <summary>Role performed by a meeting invocation.</summary>
public enum MeetingInvocationRole
{
    Participant,
    Host,
    Selector,
    Summarizer
}
