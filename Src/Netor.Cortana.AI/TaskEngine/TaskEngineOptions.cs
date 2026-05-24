using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine;

/// <summary>
/// P4 任务执行引擎配置参数。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §14。
/// </summary>
public sealed class TaskEngineOptions
{
    /// <summary>单步超时。</summary>
    public TimeSpan PerStepTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>整个任务超时。</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 最大并行步骤数（避免同时创建太多子智能体）。
    /// 详见 doc 04 §14 Q2。
    /// </summary>
    public int MaxParallelSteps { get; init; } = 5;

    /// <summary>
    /// 全局 LLM 并发上限（SemaphoreSlim 容量）。
    /// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/07-P4补充-资源管理与工具授权.md §1。
    /// </summary>
    public int MaxLlmConcurrency { get; init; } = 5;

    /// <summary>默认重试策略。</summary>
    public RetryPolicy DefaultRetryPolicy { get; init; } = new();

    /// <summary>
    /// 是否自动确认计划（跳过用户确认环节）。
    /// true：计划生成后直接进入执行阶段（适合简单任务/测试）。
    /// false（默认）：等待用户确认后才执行。
    /// </summary>
    public bool AutoConfirmPlan { get; init; }
}
