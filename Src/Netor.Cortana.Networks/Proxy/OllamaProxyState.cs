namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 兼容代理运行状态。
/// </summary>
public enum OllamaProxyStatus
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Disabled = 4,
    PortInUse = 5,
    AccessDenied = 6,
    BackendUnavailable = 7,
    Failed = 8
}

/// <summary>
/// Ollama 兼容代理状态快照。
/// </summary>
public sealed record OllamaProxyStateSnapshot(
    OllamaProxyStatus Status,
    string Host,
    int Port,
    string Url,
    bool IsRunning,
    string LastError,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt);
