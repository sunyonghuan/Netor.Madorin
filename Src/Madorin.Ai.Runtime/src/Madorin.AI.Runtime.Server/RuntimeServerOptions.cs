using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server;

public sealed record RuntimeServerOptions
{
    public RuntimeServerOptions(string workspaceRoot)
    {
        WorkspaceRoot = workspaceRoot;
        DataDirectory = Path.Combine(workspaceRoot, ".madorin");
        ConfigDirectory = Path.Combine(DataDirectory, "config");
        LogDirectory = Path.Combine(DataDirectory, "logs");
    }

    public RuntimeServerOptions(
        string workspaceRoot,
        string dataDirectory,
        string configDirectory,
        string logDirectory,
        string instanceId,
        string pipePrefix,
        int maxConcurrentRuns)
        : this(workspaceRoot)
    {
        DataDirectory = dataDirectory;
        ConfigDirectory = configDirectory;
        LogDirectory = logDirectory;
        InstanceId = instanceId;
        PipePrefix = pipePrefix;
        MaxConcurrentRuns = maxConcurrentRuns;
    }

    public string WorkspaceRoot { get; init; }

    public string DataDirectory { get; init; }

    public string ConfigDirectory { get; init; }

    public string LogDirectory { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public string PipePrefix { get; init; } = "madorin.ai.runtime";

    public int MaxConcurrentRuns { get; init; } = 4;

    public int MaxConcurrentRunsPerProvider { get; init; } = 4;

    public int MaxConcurrentRunsPerSession { get; init; } = 1;

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public int HeartbeatIntervalSeconds { get; init; } = 15;

    public TimeSpan HostLeaseTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public TimeSpan HandshakeClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    public RuntimeLimits BlobLimits { get; init; } = new();

    public long MaxGlobalBlobBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>Handshake secret captured when the server starts; null means read the environment.</summary>
    public string? HandshakeSecret { get; init; }

    public string? MemoryUserHome { get; init; }

    public Func<NextTurnSelection, IRuntimeProviderAdapter>? ProviderResolver { get; init; }
}
