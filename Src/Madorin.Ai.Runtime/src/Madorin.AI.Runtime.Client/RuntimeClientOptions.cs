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
    RuntimeCapabilities? Capabilities = null,
    /// <summary>Absolute path to the Runtime executable; used when the SDK needs to launch a process.</summary>
    string? RuntimeExecutablePath = null,
    /// <summary>Expected Runtime semantic version; validated during handshake.</summary>
    string? ExpectedRuntimeVersion = null,
    /// <summary>Root workspace directory passed to the launched Runtime process.</summary>
    string? WorkspaceDirectory = null,
    /// <summary>Directory for Runtime persistent data; null lets the Runtime choose its default.</summary>
    string? DataDirectory = null,
    /// <summary>Directory for Runtime log output; null lets the Runtime choose its default.</summary>
    string? LogDirectory = null,
    /// <summary>Unique identifier for the Runtime instance this client targets.</summary>
    string? RuntimeInstanceId = null,
    /// <summary>Named-pipe prefix used for discovery and connection.</summary>
    string PipePrefix = "madorin.ai.runtime",
    /// <summary>Controls whether the client attaches to an existing Runtime or starts a new one.</summary>
    RuntimeStartPolicy StartPolicy = RuntimeStartPolicy.AttachExisting,
    /// <summary>Maximum time to wait for a Runtime instance to become ready.</summary>
    TimeSpan StartupTimeout = default,
    /// <summary>How the owned Runtime process is stopped when the client is disposed.</summary>
    OwnedProcessClosePolicy OwnedProcessClosePolicy = OwnedProcessClosePolicy.Shutdown,
    /// <summary>Maximum time to wait for the owned process to exit gracefully before force-killing.</summary>
    TimeSpan OwnedProcessShutdownTimeout = default,
    /// <summary>Typed handlers for Runtime-initiated host callbacks.</summary>
    RuntimeHostCallbacks? HostCallbacks = null)
{
    public string ResolvedEventPipeName => EventPipeName ?? $"{PipeName}.events";

    public TimeSpan ResolvedReconnectWindow => ReconnectWindow == default
        ? TimeSpan.FromSeconds(15)
        : ReconnectWindow;

    public TimeSpan ResolvedReconnectDelay => ReconnectDelay == default
        ? TimeSpan.FromMilliseconds(100)
        : ReconnectDelay;

    /// <summary>Effective startup timeout (15 s when unspecified).</summary>
    public TimeSpan ResolvedStartupTimeout => StartupTimeout == default
        ? TimeSpan.FromSeconds(15)
        : StartupTimeout;

    /// <summary>Effective owned-process shutdown timeout (5 s when unspecified).</summary>
    public TimeSpan ResolvedOwnedProcessShutdownTimeout => OwnedProcessShutdownTimeout == default
        ? TimeSpan.FromSeconds(5)
        : OwnedProcessShutdownTimeout;
}
