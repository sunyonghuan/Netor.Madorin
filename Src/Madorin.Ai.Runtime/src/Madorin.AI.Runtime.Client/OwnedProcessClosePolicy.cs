namespace Madorin.AI.Runtime.Client;

/// <summary>
/// Determines what happens to a Runtime child process that this client owns when the client is disposed.
/// </summary>
public enum OwnedProcessClosePolicy
{
    /// <summary>Leave the owned process running after dispose.</summary>
    KeepAlive = 0,

    /// <summary>Gracefully stop the owned process; force-kill after a timeout.</summary>
    Shutdown = 1,
}
