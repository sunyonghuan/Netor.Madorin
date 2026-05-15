namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// Workflow 执行器阈值参数。阶段 2B 采用常量形式，阶段 5B+ 起部分迁移到 SystemSettingsService。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B.2。
/// </summary>
public sealed class WorkflowExecutorOptions
{
    /// <summary>编排最大轮次（GroupChat / Magentic 共用）。</summary>
    public int MaxRounds { get; init; } = 8;

    /// <summary>单步超时。</summary>
    public TimeSpan PerStepTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>整个任务超时。</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 是否要求 Magentic 计划必须人工签字才能继续执行（阶段 5B+ HITL 节点）。
    /// 阶段 2B 占位实现不接入。
    /// </summary>
    public bool MagenticRequirePlanSignoff { get; init; } = false;

    /// <summary>任务标题最大长度。</summary>
    public int TaskTitleMaxLength { get; init; } = 32;

    /// <summary>Magentic 重规划次数上限。</summary>
    public int MaxReplans { get; init; } = 2;
}
