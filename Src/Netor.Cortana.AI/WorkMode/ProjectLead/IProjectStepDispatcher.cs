using Netor.Cortana.AI.WorkMode.Files;

namespace Netor.Cortana.AI.WorkMode.ProjectLead;

/// <summary>
/// B 层项目组长用于派发单个计划步骤的抽象。
/// 阶段 1 先抽象出边界，真实 C 层 Agent 派发在下一步接入。
/// </summary>
public interface IProjectStepDispatcher
{
    Task<string> DispatchAsync(
        string taskId,
        WorkTaskPlanStepFile step,
        WorkTaskEnvironmentFile? environment,
        CancellationToken cancellationToken);
}
