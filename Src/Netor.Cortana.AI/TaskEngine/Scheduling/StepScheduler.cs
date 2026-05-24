using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Scheduling;

/// <summary>
/// 步骤调度器实现：根据依赖关系和执行模式决定下一批可执行的步骤。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.2。
/// </summary>
public sealed class StepScheduler : IStepScheduler
{
    /// <inheritdoc/>
    public IReadOnlyList<PlanStep> GetReadySteps(ExecutionPlan plan)
    {
        var ready = new List<PlanStep>();

        foreach (var step in plan.Steps)
        {
            // 只考虑 Pending 或 WaitingDeps 状态的步骤
            if (step.Status is not (PlanStepStatus.Pending or PlanStepStatus.WaitingDeps))
                continue;

            // 如果是 await_user 模式且还没有被用户确认过，标记为 WaitingUser
            if (step.ExecutionMode == "await_user" && step.Status == PlanStepStatus.Pending)
            {
                step.Status = PlanStepStatus.WaitingUser;
                continue;
            }

            // 检查所有依赖步骤是否已完成
            var allDepsCompleted = step.DependsOn.Count == 0 || step.DependsOn.All(depId =>
                plan.Steps.Any(s => s.StepId == depId
                    && s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped));

            if (allDepsCompleted)
            {
                ready.Add(step);
            }
            else if (step.Status == PlanStepStatus.Pending)
            {
                // 有依赖未满足，标记为 WaitingDeps
                step.Status = PlanStepStatus.WaitingDeps;
            }
        }

        return ready;
    }

    /// <inheritdoc/>
    public bool IsAllCompleted(ExecutionPlan plan)
        => plan.Steps.Count > 0 && plan.Steps.All(s =>
            s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped or PlanStepStatus.Cancelled);

    /// <inheritdoc/>
    public bool IsWaitingUser(ExecutionPlan plan)
        => plan.Steps.Any(s => s.Status == PlanStepStatus.WaitingUser);

    /// <inheritdoc/>
    public bool HasFailedSteps(ExecutionPlan plan)
        => plan.Steps.Any(s => s.Status == PlanStepStatus.Failed);
}
