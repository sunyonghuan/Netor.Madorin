namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Workflow limits and failure behavior for Work mode.
/// Defaults match the V1 implementation guide (depth 3/hard 10, steps 50/hard 500).
/// </summary>
public sealed record WorkflowPolicy(
    int MaxAgentDepth = 3,
    int HardMaxAgentDepth = 10,
    int MaxStepsPerRun = 50,
    int HardMaxStepsPerRun = 500,
    int MaxSubAgentJobs = 20,
    int MaxConcurrentSteps = 4,
    int StepTimeoutSeconds = 300,
    WorkStepFailurePolicy FailurePolicy = WorkStepFailurePolicy.Stop,
    int MaxRetriesPerStep = 2,
    int RetryBackoffMilliseconds = 1000,
    WorkLoopDetection LoopDetection = WorkLoopDetection.Strict,
    bool RequireHumanConfirmationOnLoop = true)
{
    public static WorkflowPolicy Default { get; } = new();

    public void Validate()
    {
        if (MaxAgentDepth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAgentDepth),
                "MaxAgentDepth must be at least 1.");
        }

        if (HardMaxAgentDepth < MaxAgentDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxAgentDepth),
                "HardMaxAgentDepth must be greater than or equal to MaxAgentDepth.");
        }

        if (HardMaxAgentDepth > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxAgentDepth),
                "HardMaxAgentDepth cannot exceed 10.");
        }

        if (MaxStepsPerRun < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxStepsPerRun),
                "MaxStepsPerRun must be at least 1.");
        }

        if (HardMaxStepsPerRun < MaxStepsPerRun)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxStepsPerRun),
                "HardMaxStepsPerRun must be greater than or equal to MaxStepsPerRun.");
        }

        if (HardMaxStepsPerRun > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardMaxStepsPerRun),
                "HardMaxStepsPerRun cannot exceed 500.");
        }

        if (MaxSubAgentJobs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSubAgentJobs),
                "MaxSubAgentJobs must be at least 1.");
        }

        if (MaxConcurrentSteps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentSteps),
                "MaxConcurrentSteps must be at least 1.");
        }

        if (StepTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StepTimeoutSeconds),
                "StepTimeoutSeconds must be at least 1.");
        }

        if (MaxRetriesPerStep < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetriesPerStep),
                "MaxRetriesPerStep cannot be negative.");
        }

        if (RetryBackoffMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetryBackoffMilliseconds),
                "RetryBackoffMilliseconds cannot be negative.");
        }
    }
}

/// <summary>How work-mode history is projected into manager and child invocations.</summary>
public sealed record WorkContextPolicy(
    bool PreservePlan = true,
    bool PreserveCurrentStep = true,
    bool PreserveToolResults = true,
    bool PreserveReasoning = false,
    int? MaxTokens = null,
    int? TailMessageCount = null)
{
    public static WorkContextPolicy Default { get; } = new();
}
