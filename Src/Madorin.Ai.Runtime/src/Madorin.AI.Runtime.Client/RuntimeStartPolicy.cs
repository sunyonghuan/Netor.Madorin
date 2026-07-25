namespace Madorin.AI.Runtime.Client;

/// <summary>
/// Controls how the client SDK discovers or launches a Runtime instance.
/// </summary>
public enum RuntimeStartPolicy
{
    /// <summary>Connect to an already-running Runtime; fail if none is found.</summary>
    AttachExisting = 0,

    /// <summary>Connect to a running Runtime when available; otherwise start a new process.</summary>
    StartIfMissing = 1,

    /// <summary>Always start a new Runtime process.</summary>
    AlwaysStart = 2,
}
