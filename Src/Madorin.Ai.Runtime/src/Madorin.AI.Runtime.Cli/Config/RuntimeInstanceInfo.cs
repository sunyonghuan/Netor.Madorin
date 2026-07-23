namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Written by <c>madorin serve</c> to <c>.madorin/runtime.pid</c> while the server is running
/// so that <c>madorin run</c> can discover the active Named Pipe endpoint.
/// </summary>
public sealed record RuntimeInstanceInfo(
    string InstanceId,
    string PipeName,
    string EventPipeName,
    int Pid,
    DateTimeOffset StartedAt);
