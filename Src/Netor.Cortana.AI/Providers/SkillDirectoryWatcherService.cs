using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// Watches skill directories and advances the tool context version when skill files change.
/// </summary>
public sealed class SkillDirectoryWatcherService(
    IAppPaths appPaths,
    ToolContextVersionService toolContextVersionService,
    ISubscriber subscriber,
    ILogger<SkillDirectoryWatcherService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private FileSystemWatcher? _userSkillsWatcher;
    private FileSystemWatcher? _workspaceSkillsWatcher;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RestartWatchers();

        subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
        {
            RestartWorkspaceWatcher(args.Path);
            return Task.FromResult(false);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatchers();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatchers();
    }

    private void RestartWatchers()
    {
        lock (_gate)
        {
            _userSkillsWatcher = RestartWatcher(_userSkillsWatcher, appPaths.UserSkillsDirectory, "用户技能目录");
            _workspaceSkillsWatcher = RestartWatcher(_workspaceSkillsWatcher, appPaths.WorkspaceSkillsDirectory, "工作区技能目录");
        }
    }

    private void RestartWorkspaceWatcher(string workspaceDirectory)
    {
        lock (_gate)
        {
            var skillsDirectory = Path.Combine(workspaceDirectory, ".cortana", "skills");
            _workspaceSkillsWatcher = RestartWatcher(_workspaceSkillsWatcher, skillsDirectory, "工作区技能目录");
        }
    }

    private FileSystemWatcher RestartWatcher(FileSystemWatcher? existing, string directory, string scope)
    {
        DisposeWatcher(existing);

        Directory.CreateDirectory(directory);
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };

        watcher.Changed += OnSkillDirectoryChanged;
        watcher.Created += OnSkillDirectoryChanged;
        watcher.Deleted += OnSkillDirectoryChanged;
        watcher.Renamed += OnSkillDirectoryRenamed;
        watcher.EnableRaisingEvents = true;

        logger.LogInformation("{Scope}监听已启动：{Directory}", scope, directory);
        return watcher;
    }

    private void StopWatchers()
    {
        lock (_gate)
        {
            DisposeWatcher(_userSkillsWatcher);
            DisposeWatcher(_workspaceSkillsWatcher);
            _userSkillsWatcher = null;
            _workspaceSkillsWatcher = null;

            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnSkillDirectoryChanged;
        watcher.Created -= OnSkillDirectoryChanged;
        watcher.Deleted -= OnSkillDirectoryChanged;
        watcher.Renamed -= OnSkillDirectoryRenamed;
        watcher.Dispose();
    }

    private void OnSkillDirectoryChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleBump(ResolveScope(sender));
    }

    private void OnSkillDirectoryRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleBump(ResolveScope(sender));
    }

    private string ResolveScope(object sender)
    {
        if (ReferenceEquals(sender, _userSkillsWatcher))
        {
            return "用户技能目录";
        }

        return "工作区技能目录";
    }

    private void ScheduleBump(string scope)
    {
        lock (_gate)
        {
            if (_timers.Remove(scope, out var existing))
            {
                existing.Dispose();
            }

            _timers[scope] = new Timer(OnDebounceElapsed, scope, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (state is not string scope)
        {
            return;
        }

        lock (_gate)
        {
            if (_timers.Remove(scope, out var timer))
            {
                timer.Dispose();
            }
        }

        var version = toolContextVersionService.Bump();
        logger.LogInformation("{Scope}已变更，工具上下文版本推进到 {Version}", scope, version);
    }
}
