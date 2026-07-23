namespace Madorin.AI.Runtime.Contracts;

public sealed record InitializeResponse(
    string RuntimeInstanceId,
    string ProgramVersion,
    string SelectedProtocolVersion,
    RuntimeCapabilities Capabilities,
    RuntimeLimits Limits,
    string EventChannelAddress,
    int HeartbeatIntervalSeconds);
