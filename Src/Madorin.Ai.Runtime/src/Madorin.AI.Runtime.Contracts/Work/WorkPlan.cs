using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>Structured plan produced by the general manager before step execution.</summary>
public sealed record WorkPlanDraft(
    string PlanVersion,
    string Goal,
    WorkPlanStepDraft[] Steps,
    string? Summary = null);

/// <summary>One planned work step before persistence and scheduling.</summary>
public sealed record WorkPlanStepDraft(
    string StepId,
    string Goal,
    string TargetAgentId,
    string[]? DependsOn = null,
    string? ParentStepId = null,
    int Depth = 0,
    string? Title = null,
    JsonElement? Inputs = null,
    bool IsBackground = false);

/// <summary>Persisted/resumable snapshot of one work-plan step.</summary>
public sealed record WorkStepSnapshot(
    string StepId,
    string PlanVersion,
    string SessionId,
    string RunId,
    string TargetAgentId,
    string Goal,
    WorkStepLifecycleStatus Status,
    string StepInputHash,
    int Depth = 0,
    string? ParentStepId = null,
    string? InvocationId = null,
    string? ResultJson = null,
    string? ErrorMessage = null,
    int AttemptCount = 0,
    string[]? DependsOn = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

/// <summary>One execution attempt of a work step.</summary>
public sealed record WorkStepAttemptSnapshot(
    string StepId,
    string PlanVersion,
    int AttemptNumber,
    string InvocationId,
    WorkStepLifecycleStatus Status,
    string? ErrorMessage = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

/// <summary>Background job linked to a work step.</summary>
public sealed record WorkBackgroundJobSnapshot(
    string JobId,
    string SessionId,
    string RunId,
    string StepId,
    string PlanVersion,
    WorkBackgroundJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? ErrorMessage = null);

/// <summary>Durable work state returned by session.resume.</summary>
public sealed record WorkResumeState(
    WorkSessionStatus Status,
    string GeneralManagerId,
    string PlanVersion,
    string? PlanMessageId,
    WorkStepSnapshot[] Steps,
    WorkStepSnapshot? CurrentStep,
    WorkBackgroundJobSnapshot[]? BackgroundJobs = null,
    string[]? SentToolCallIds = null,
    bool RequiresManualIntervention = false,
    string? PendingApprovalRequestId = null);
