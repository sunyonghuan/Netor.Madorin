using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 监听 Agent 文件目录变化，并按 Agent 目录做 200ms 去抖刷新。
/// </summary>
public sealed class AgentFileWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly AgentFileIndex _index;
    private readonly ILogger<AgentFileWatcher> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private string _agentsDirectory = string.Empty;

    public event EventHandler<string>? AgentChanged;

    public AgentFileWatcher(AgentFileIndex index, ILogger<AgentFileWatcher> logger)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start(string agentsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsDirectory);

        Directory.CreateDirectory(agentsDirectory);
        Stop();

        _agentsDirectory = agentsDirectory;
        _watcher = new FileSystemWatcher(agentsDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_gate)
        {
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var agentName = ResolveAgentName(e.FullPath);
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            Schedule(agentName);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldName = ResolveAgentName(e.OldFullPath);
        if (!string.IsNullOrWhiteSpace(oldName))
        {
            Schedule(oldName);
        }

        var newName = ResolveAgentName(e.FullPath);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            Schedule(newName);
        }
    }

    private string? ResolveAgentName(string path)
    {
        if (string.IsNullOrWhiteSpace(_agentsDirectory))
        {
            return null;
        }

        var relative = Path.GetRelativePath(_agentsDirectory, path);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        var firstSegment = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.Equals(firstSegment, ".trash", StringComparison.Ordinal) ? null : firstSegment;
    }

    private void Schedule(string agentName)
    {
        lock (_gate)
        {
            if (_timers.Remove(agentName, out var existing))
            {
                existing.Dispose();
            }

            _timers[agentName] = new Timer(OnTimer, agentName, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        if (state is not string agentName)
        {
            return;
        }

        lock (_gate)
        {
            if (_timers.Remove(agentName, out var timer))
            {
                timer.Dispose();
            }
        }

        try
        {
            var directory = Path.Combine(_agentsDirectory, agentName);
            if (Directory.Exists(directory))
            {
                _index.RefreshDirectory(directory);
            }
            else
            {
                _index.Remove(agentName);
            }

            AgentChanged?.Invoke(this, agentName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "刷新 Agent 文件索引失败：{AgentName}", agentName);
        }
    }
}
