namespace Madorin.AI.Runtime.Contracts;

public sealed record InitializeRequest(
    string HostInstanceId,
    string ClientVersion,
    IReadOnlyList<string> SupportedProtocolVersions,
    RuntimeCapabilities ExpectedCapabilities,
    long LastConfirmedGsn);
