namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 任务状态摘要信息（列表查询 / 状态查询用）。
/// 由 <see cref="TaskExecutionEngine.GetTaskStatusAsync"/> 和
/// <see cref="TaskExecutionEngine.ListTasksAsync"/> 返回。
/// </summary>
public sealed class TaskStatusInfo
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>任务状态：running / paused / completed / failed / cancelled。</summary>
    public required string Status { get; init; }

    /// <summary>任务标题。</summary>
    public string? Title { get; init; }

    /// <summary>任务启动时间。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>计划状态（运行中任务有值）。</summary>
    public PlanStatus? PlanStatus { get; init; }

    /// <summary>已完成步骤数（运行中任务有值）。</summary>
    public int CompletedSteps { get; init; }

    /// <summary>总步骤数（运行中任务有值）。</summary>
    public int TotalSteps { get; init; }
}

/// <summary>
/// 任务详情信息（包含执行计划和需求分析）。
/// 由 <see cref="TaskExecutionEngine.GetTaskDetailAsync"/> 返回。
/// </summary>
public sealed class TaskDetailInfo
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>任务标题。</summary>
    public required string Title { get; init; }

    /// <summary>任务状态。</summary>
    public required string Status { get; init; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>执行计划（可能为 null，如果还在需求分析阶段）。</summary>
    public ExecutionPlan? Plan { get; init; }

    /// <summary>需求分析结果（可能为 null，如果还未完成需求分析）。</summary>
    public RequirementsAnalysis? Requirements { get; init; }
}
