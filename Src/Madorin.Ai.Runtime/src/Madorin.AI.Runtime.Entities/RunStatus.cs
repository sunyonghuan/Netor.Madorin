namespace Madorin.AI.Runtime.Entities;

public enum RunStatus
{
    Accepted,
    Preparing,
    Running,
    WaitingForTool,
    WaitingForApproval,
    WaitingForCredentials,
    Persisting,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}
