using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Client;

public sealed record RuntimeClientOptions(
    string PipeName,
    string HostInstanceId,
    string ClientVersion = "0.1.0",
    TimeSpan ConnectTimeout = default,
    string? EventPipeName = null,
    string? ExpectedRuntimeInstanceId = null,
    string? HandshakeSecret = null,
    int? ExpectedServerProcessId = null,
    TimeSpan HeartbeatInterval = default,
    TimeSpan ReconnectWindow = default,
    TimeSpan ReconnectDelay = default,
    bool EnableBackgroundHeartbeat = true,
    RuntimeCapabilities? Capabilities = null)
{
    public string ResolvedEventPipeName => EventPipeName ?? $"{PipeName}.events";

    public TimeSpan ResolvedReconnectWindow => ReconnectWindow == default
        ? TimeSpan.FromSeconds(15)
        : ReconnectWindow;

    public TimeSpan ResolvedReconnectDelay => ReconnectDelay == default
        ? TimeSpan.FromMilliseconds(100)
        : ReconnectDelay;
}
