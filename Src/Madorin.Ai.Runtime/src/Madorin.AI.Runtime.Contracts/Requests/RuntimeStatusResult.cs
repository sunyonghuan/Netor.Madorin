namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeStatusResult(
    string RuntimeInstanceId,
    string ProgramVersion,
    string ProtocolVersion,
    string Workspace,
    int ProcessId,
    DateTimeOffset StartedAt,
    int ActiveRunCount,
    int ConnectedHostCount,
    int ConnectedEventChannelCount);
