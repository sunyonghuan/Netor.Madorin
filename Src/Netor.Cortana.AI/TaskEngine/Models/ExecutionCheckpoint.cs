namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 断点检查点。进程重启时用于恢复任务执行状态。
/// 每步完成后更新，保存到 checkpoint.json。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §7.2。
/// </summary>
public sealed class ExecutionCheckpoint
{
    /// <summary>任务 ID。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>对应的计划版本号（恢复时校验一致性）。</summary>
    public int PlanVersion { get; set; }

    /// <summary>
    /// 当前执行阶段：
    /// <list type="bullet">
    ///   <item><c>"requirements"</c> — 需求分析阶段</item>
    ///   <item><c>"planning"</c> — 计划制定阶段</item>
    ///   <item><c>"executing"</c> — 执行阶段</item>
    ///   <item><c>"validating"</c> — 验证阶段</item>
    /// </list>
    /// </summary>
    public string CurrentPhase { get; set; } = string.Empty;

    /// <summary>当前正在执行的步骤 ID（可为 null，如在阶段切换间隙）。</summary>
    public string? CurrentStepId { get; set; }

    /// <summary>检查点保存时间。</summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>各步骤的状态快照（StepId → Status）。</summary>
    public Dictionary<string, PlanStepStatus> StepStatuses { get; set; } = new();
}
