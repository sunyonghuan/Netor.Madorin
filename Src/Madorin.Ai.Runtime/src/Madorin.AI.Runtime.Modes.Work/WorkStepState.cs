namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Tracks the persisted identity and execution state of one work-plan step.</summary>
public sealed record WorkStepState(
    string StepId,
    string PlanVersion,
    string TargetAgentId,
    WorkStepStatus Status,
    string? ResultJson = null);

/// <summary>Describes the execution status of a work-plan step.</summary>
public enum WorkStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Interrupted
}
