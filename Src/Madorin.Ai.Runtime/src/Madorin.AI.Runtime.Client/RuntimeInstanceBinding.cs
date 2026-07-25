namespace Madorin.AI.Runtime.Client;

/// <summary>
/// Immutable snapshot of the binding between a <see cref="RuntimeClient"/> and its Runtime instance, exposed only after authentication and initialisation have succeeded.
/// </summary>
public sealed record RuntimeInstanceBinding(
    /// <summary>The host-side instance identifier supplied at construction time.</summary>
    string HostInstanceId,

    /// <summary>The Runtime-side instance identifier reported during handshake.</summary>
    string? RuntimeInstanceId,

    /// <summary>The normalised workspace directory, or <c>null</c> when unspecified.</summary>
    string? Workspace,

    /// <summary>The operating-system PID of the connected Runtime (or the server side of the pipe).</summary>
    int? RuntimeProcessId,

    /// <summary>The control-channel named-pipe address.</summary>
    string ControlPipeName,

    /// <summary>The event-channel named-pipe address.</summary>
    string EventPipeName,

    /// <summary><c>true</c> when this client started the Runtime process and owns its lifecycle.</summary>
    bool IsProcessOwned);
