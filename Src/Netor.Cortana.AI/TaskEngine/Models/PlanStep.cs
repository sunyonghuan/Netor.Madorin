namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 执行计划中的单个步骤。
/// 步骤可以包含子任务（并行执行时拆分为多个子任务）。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §4.2。
/// </summary>
public sealed class PlanStep
{
    /// <summary>步骤唯一 ID。</summary>
    public required string StepId { get; init; }

    /// <summary>步骤序号（1-based，用于 UI 显示）。</summary>
    public int Sequence { get; set; }

    /// <summary>步骤标题（一行摘要，UI 展示用）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>步骤详细描述（给子智能体的完整指令）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 执行模式：
    /// <list type="bullet">
    ///   <item><c>"sequential"</c> — 顺序执行（默认）</item>
    ///   <item><c>"parallel"</c> — 内部子任务并行执行</item>
    ///   <item><c>"await_user"</c> — 执行前等待用户确认</item>
    /// </list>
    /// </summary>
    public string ExecutionMode { get; set; } = "sequential";

    /// <summary>
    /// 依赖的步骤 ID 列表。
    /// 当前步骤必须等所有依赖步骤完成后才能开始。
    /// 空列表表示无依赖。
    /// </summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>子任务列表（ExecutionMode="parallel" 时使用）。</summary>
    public List<PlanSubTask> SubTasks { get; set; } = [];

    /// <summary>
    /// 需要创建的子智能体类型描述。
    /// 主智能体根据此描述动态生成子智能体的 instructions。
    /// 例如："市场数据采集专家，擅长从公开财报和行业报告中提取关键数据"
    /// </summary>
    public string AgentTypeDescription { get; set; } = string.Empty;

    /// <summary>该步骤需要的工具列表（子智能体将获得这些工具的访问权限）。</summary>
    public List<string> RequiredTools { get; set; } = [];

    /// <summary>是否需要用户确认后才继续下一步（耗时步骤标记为 true）。</summary>
    public bool RequireUserConfirmation { get; set; }

    /// <summary>该步骤的最大重试次数（覆盖全局 RetryPolicy.MaxRetries）。</summary>
    public int MaxRetries { get; set; } = 3;

    // ──── 运行时状态（执行过程中更新） ────

    /// <summary>步骤执行状态。</summary>
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;

    /// <summary>实际分配的子智能体名称（运行时填充）。</summary>
    public string? AssignedAgentName { get; set; }

    /// <summary>步骤开始时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>步骤完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>执行结果摘要（给用户看，UI 展示用）。</summary>
    public string? ResultSummary { get; set; }

    /// <summary>执行结果详情（给后续步骤的子智能体作为上下文）。</summary>
    public string? ResultDetail { get; set; }

    /// <summary>错误信息（失败时）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>进度百分比（0-100，长步骤可中途更新）。</summary>
    public int ProgressPercent { get; set; }
}

/// <summary>步骤执行状态。</summary>
public enum PlanStepStatus
{
    /// <summary>等待执行。</summary>
    Pending,

    /// <summary>等待依赖步骤完成。</summary>
    WaitingDeps,

    /// <summary>等待用户确认开始。</summary>
    WaitingUser,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>重试中。</summary>
    Retrying,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>失败（重试耗尽）。</summary>
    Failed,

    /// <summary>被跳过（用户修改计划时标记）。</summary>
    Skipped,

    /// <summary>用户取消。</summary>
    Cancelled,
}

/// <summary>
/// 并行步骤中的子任务。
/// 当 <see cref="PlanStep.ExecutionMode"/> 为 "parallel" 时，
/// 步骤内部可以拆分为多个并行子任务，每个子任务由独立的子智能体执行。
/// </summary>
public sealed class PlanSubTask
{
    /// <summary>子任务唯一 ID。</summary>
    public required string SubTaskId { get; init; }

    /// <summary>子任务标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>子任务描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>子智能体类型描述。</summary>
    public string AgentTypeDescription { get; set; } = string.Empty;

    /// <summary>所需工具列表。</summary>
    public List<string> RequiredTools { get; set; } = [];

    /// <summary>子任务执行状态。</summary>
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;

    /// <summary>实际分配的子智能体名称。</summary>
    public string? AssignedAgentName { get; set; }

    /// <summary>子任务结果摘要。</summary>
    public string? ResultSummary { get; set; }

    /// <summary>错误信息。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; set; }
}
