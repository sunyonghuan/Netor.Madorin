using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Core;

public static class RunStateMachine
{
    public static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Completed
            or RunStatus.Failed
            or RunStatus.Cancelled
            or RunStatus.Interrupted;

    public static bool CanTransition(RunStatus current, RunStatus next)
    {
        if (IsTerminal(current))
        {
            return false;
        }

        if (next is RunStatus.Cancelled)
        {
            return true;
        }

        return (current, next) switch
        {
            (RunStatus.Accepted, RunStatus.Preparing) => true,
            (RunStatus.Preparing, RunStatus.Running or RunStatus.Failed) => true,
            (RunStatus.Running, RunStatus.WaitingForTool
                or RunStatus.WaitingForApproval
                or RunStatus.WaitingForCredentials
                or RunStatus.Persisting
                or RunStatus.Failed) => true,
            (RunStatus.WaitingForTool, RunStatus.Running
                or RunStatus.WaitingForApproval
                or RunStatus.Failed) => true,
            (RunStatus.WaitingForApproval, RunStatus.Running
                or RunStatus.WaitingForTool
                or RunStatus.Failed) => true,
            (RunStatus.WaitingForCredentials, RunStatus.Running or RunStatus.Failed) => true,
            (RunStatus.Persisting, RunStatus.Completed or RunStatus.Failed) => true,
            _ => false
        };
    }
}
