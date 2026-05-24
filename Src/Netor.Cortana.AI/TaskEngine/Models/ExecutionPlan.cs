namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 任务执行计划。由计划制定子智能体生成，用户确认后执行。
/// 落盘为 JSON 文件（程序恢复用）+ Markdown 文件（用户查看用）。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §4.1。
/// </summary>
public sealed class ExecutionPlan
{
    /// <summary>计划唯一 ID（关联 TaskId）。</summary>
    public required string PlanId { get; init; }

    /// <summary>关联的 Workflow 任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>计划版本号（每次用户修改 +1，用于恢复时校验一致性）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>任务总体描述（一句话）。</summary>
    public string TaskSummary { get; set; } = string.Empty;

    /// <summary>需求分析结果（由需求分析子智能体产出）。</summary>
    public RequirementsAnalysis? Requirements { get; set; }

    /// <summary>计划步骤列表（有序）。</summary>
    public List<PlanStep> Steps { get; set; } = [];

    /// <summary>最终目标描述（验证子智能体用来判断任务是否完成）。</summary>
    public string FinalGoal { get; set; } = string.Empty;

    /// <summary>计划创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>计划状态。</summary>
    public PlanStatus Status { get; set; } = PlanStatus.Draft;

    /// <summary>
    /// 来源模板 ID（如果是基于模板创建的）。
    /// null 表示从零开始。
    /// </summary>
    public string? SourceTemplateId { get; set; }
}

/// <summary>计划整体状态。</summary>
public enum PlanStatus
{
    /// <summary>草稿（正在和用户对话制定中）。</summary>
    Draft,

    /// <summary>用户已确认，等待执行。</summary>
    Confirmed,

    /// <summary>执行中。</summary>
    Executing,

    /// <summary>用户暂停（可修改）。</summary>
    Paused,

    /// <summary>全部完成。</summary>
    Completed,

    /// <summary>执行失败（所有重试耗尽）。</summary>
    Failed,

    /// <summary>用户取消。</summary>
    Cancelled,
}
