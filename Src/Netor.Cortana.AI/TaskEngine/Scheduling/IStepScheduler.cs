using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Scheduling;

/// <summary>
/// 步骤调度器接口：根据依赖关系和执行模式决定下一批可执行的步骤。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.2。
/// </summary>
public interface IStepScheduler
{
    /// <summary>
    /// 获取当前可执行的步骤批次（依赖已满足 + 不在等待/运行中）。
    /// 返回的步骤可以并行执行。
    /// </summary>
    IReadOnlyList<PlanStep> GetReadySteps(ExecutionPlan plan);

    /// <summary>检查计划中的所有步骤是否都已完成（含跳过/取消）。</summary>
    bool IsAllCompleted(ExecutionPlan plan);

    /// <summary>检查是否有步骤正在等待用户确认。</summary>
    bool IsWaitingUser(ExecutionPlan plan);

    /// <summary>检查是否有步骤已失败（重试耗尽）。</summary>
    bool HasFailedSteps(ExecutionPlan plan);
}
