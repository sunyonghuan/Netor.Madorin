using Netor.Cortana.AI.WorkMode.Files;

namespace Netor.Cortana.AI.WorkMode.ProjectLead;

/// <summary>
/// C 层真实派发尚未接入前的占位派发器。
/// </summary>
public sealed class PendingProjectStepDispatcher : IProjectStepDispatcher
{
    public Task<string> DispatchAsync(
        string taskId,
        WorkTaskPlanStepFile step,
        WorkTaskEnvironmentFile? environment,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("C 层专员派发尚未接入，无法执行计划步骤。");
    }
}
