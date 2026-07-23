namespace Madorin.AI.Runtime.Contracts;

/// <summary>How a work-plan step failure affects the rest of the run.</summary>
public enum WorkStepFailurePolicy
{
    Stop,
    Continue,
    Retry,
    AskHost
}

/// <summary>Loop detection strictness for recursive or repeating work goals.</summary>
public enum WorkLoopDetection
{
    Strict,
    Lenient,
    Disabled
}

/// <summary>Lifecycle status of a work session.</summary>
public enum WorkSessionStatus
{
    Planning,
    Executing,
    Paused,
    WaitingForApproval,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>Status of one scheduled work-plan step.</summary>
public enum WorkStepLifecycleStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Interrupted,
    Skipped,
    WaitingForApproval
}

/// <summary>Status of a work-mode background job.</summary>
public enum WorkBackgroundJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}
