using Avalonia.Threading;

namespace Netor.Cortana.UI.Controls.Shared.Internal;

/// <summary>
/// <see cref="FileSystemWatcher"/> 事件的 200ms 去抖 + 合并批处理器。
///
/// 场景：AI 批量写文件时 FSW 会在极短时间内触发几十到上千条 Created/Deleted/Renamed 事件；
/// 逐条走 UI 线程刷新会把主线程打爆。批处理器把窗口内的事件按路径去重、按拓扑排序后
/// 一次性 drain 给消费方，把 O(n) UI 刷新压到 O(1)。
///
/// 契约：
/// - <see cref="Enqueue"/> / <see cref="EnqueueRenamed"/> / <see cref="EnqueueOverflow"/>
///   来自 FSW 回调线程（非 UI 线程），实现内部自持锁；
/// - drain 在 UI 线程执行；构造时传入的 <paramref name="drain"/> 回调保证在 UI 线程调用；
/// - <see cref="Clear"/> 用于工作区切换等场景丢弃未 drain 事件；
/// - <see cref="FlushNow"/> 立即触发 drain，用于装载完毕后消化装载期积累的事件；
/// - 忽略集过滤应在 Enqueue 前完成（由调用方 <see cref="WorkspaceIgnoreRules.ShouldIgnorePath"/> 保证）。
/// </summary>
internal sealed class WatcherEventBatcher
{
    private const int DebounceMs = 200;

    private readonly Action<IReadOnlyList<WatcherEventRecord>> _drain;
    private readonly object _lock = new();
    private readonly List<RawEvent> _pending = new();
    private DispatcherTimer? _timer;
    private bool _hasOverflow;
    private readonly Func<DispatcherTimer> _timerFactory;

    public WatcherEventBatcher(Action<IReadOnlyList<WatcherEventRecord>> drain)
        : this(drain, timerFactory: null)
    {
    }

    /// <summary>
    /// 测试用构造：允许注入固定 <see cref="DispatcherTimer"/> 工厂。
    /// 生产代码用无参构造。
    /// </summary>
    internal WatcherEventBatcher(
        Action<IReadOnlyList<WatcherEventRecord>> drain,
        Func<DispatcherTimer>? timerFactory)
    {
        _drain = drain ?? throw new ArgumentNullException(nameof(drain));
        _timerFactory = timerFactory ?? (() => new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        });
    }

    /// <summary>
    /// 入队常规 Created/Deleted 事件（Renamed 走 <see cref="EnqueueRenamed"/>）。
    /// </summary>
    public void Enqueue(WatcherChangeTypes kind, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        lock (_lock)
        {
            _pending.Add(new RawEvent(kind, fullPath, OldPath: null));
        }
        RestartTimer();
    }

    /// <summary>
    /// 入队 Renamed 事件。
    /// </summary>
    public void EnqueueRenamed(string oldFullPath, string newFullPath)
    {
        if (string.IsNullOrEmpty(oldFullPath) || string.IsNullOrEmpty(newFullPath)) return;
        lock (_lock)
        {
            _pending.Add(new RawEvent(WatcherChangeTypes.Renamed, newFullPath, oldFullPath));
        }
        RestartTimer();
    }

    /// <summary>
    /// 入队 FSW 缓冲区溢出信号。drain 时若命中 overflow，会丢弃其余事件只输出一条 Overflow。
    /// </summary>
    public void EnqueueOverflow()
    {
        lock (_lock)
        {
            _hasOverflow = true;
        }
        RestartTimer();
    }

    /// <summary>
    /// 清空未 drain 事件；不触发 drain 回调。工作区切换时用。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _hasOverflow = false;
        }
        StopTimer();
    }

    /// <summary>
    /// 立即在当前线程 drain 一次。由懒加载装载完毕后调用，消化装载期积累的事件。
    /// 调用方必须已在 UI 线程。
    /// </summary>
    public void FlushNow()
    {
        StopTimer();
        DrainOnUiThread();
    }

    /// <summary>
    /// 测试钩子：把窗口内已入队事件直接 drain。等价于 <see cref="FlushNow"/> 但不依赖 UI 线程校验。
    /// </summary>
    internal void FlushForTest() => DrainOnUiThread();

    private void RestartTimer()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer ??= CreateTimer();
            _timer.Stop();
            _timer.Start();
        }, DispatcherPriority.Background);
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = _timerFactory();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DrainOnUiThread();
        };
        return timer;
    }

    private void StopTimer()
    {
        // UI 线程访问。非 UI 线程调用（例如 FSW 回调）会绕行到 Dispatcher，Clear 优先满足语义。
        if (Dispatcher.UIThread.CheckAccess())
        {
            _timer?.Stop();
            return;
        }
        Dispatcher.UIThread.Post(() => _timer?.Stop(), DispatcherPriority.Background);
    }

    private void DrainOnUiThread()
    {
        List<RawEvent> events;
        bool overflow;
        lock (_lock)
        {
            if (!_hasOverflow && _pending.Count == 0) return;
            events = new List<RawEvent>(_pending);
            overflow = _hasOverflow;
            _pending.Clear();
            _hasOverflow = false;
        }

        if (overflow)
        {
            _drain(new[]
            {
                new WatcherEventRecord(WatcherEventKind.Overflow, Path: string.Empty, OldPath: null),
            });
            return;
        }

        var output = Coalesce(events);
        if (output.Count > 0)
            _drain(output);
    }

    /// <summary>
    /// 按路径去重 + 复核终态 + 按拓扑排序，产出最终动作列表。
    ///
    /// 去重规则：
    /// - 后到的 Created 覆盖之前的 Deleted（净结果：存在）；
    /// - 后到的 Deleted 覆盖之前的 Created（净结果：不存在）；
    /// - Renamed(old→new) 视作 Deleted(old) + Created(new)，参与同一表的去重；
    ///   若两条都存活且没有被其它事件覆盖，drain 出一条 Renamed(old→new)。
    /// - drain 前用 <see cref="File.Exists"/>/<see cref="Directory.Exists"/> 复核终态，翻转矛盾条目。
    ///
    /// 排序：Deleted 先出（路径长度升序 = 父在前），Created 后出（同样父在前，避免子先建）。
    /// </summary>
    private static IReadOnlyList<WatcherEventRecord> Coalesce(List<RawEvent> events)
    {
        // key = 归一化路径；value = 该路径最终状态
        var state = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);
        // 记录 old→new 的原始 rename 配对，便于最终若两端都活着时合并回 Renamed
        var renamePairs = new List<(string OldPath, string NewPath)>();

        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case WatcherChangeTypes.Created:
                    Upsert(state, ev.Path, PathAction.Created, sourceRenameNewFrom: null);
                    break;
                case WatcherChangeTypes.Deleted:
                    Upsert(state, ev.Path, PathAction.Deleted, sourceRenameNewFrom: null);
                    break;
                case WatcherChangeTypes.Renamed:
                    // Rename 拆成 Del(old) + Cre(new) 进入表
                    Upsert(state, ev.OldPath!, PathAction.Deleted, sourceRenameNewFrom: null);
                    Upsert(state, ev.Path, PathAction.Created, sourceRenameNewFrom: ev.OldPath);
                    renamePairs.Add((ev.OldPath!, ev.Path));
                    break;
            }
        }

        // 复核终态：文件系统当前实际存在与否直接决定 Created / Deleted。
        foreach (var kv in state)
        {
            var exists = File.Exists(kv.Key) || Directory.Exists(kv.Key);
            if (kv.Value.Action == PathAction.Created && !exists)
                kv.Value.Action = PathAction.Deleted;
            else if (kv.Value.Action == PathAction.Deleted && exists)
                kv.Value.Action = PathAction.Created;
        }

        // 匹配 rename 配对：仅当 (old 仍是 Deleted) 且 (new 仍是 Created 且 sourceRenameNewFrom==old)
        // 时才恢复为 Renamed 语义。恢复后从 state 里摘掉，避免重复输出。
        var renameOutputs = new List<WatcherEventRecord>();
        foreach (var (oldPath, newPath) in renamePairs)
        {
            var oldKey = Normalize(oldPath);
            var newKey = Normalize(newPath);
            if (!state.TryGetValue(oldKey, out var oldState) || !state.TryGetValue(newKey, out var newState))
                continue;
            if (oldState.Action != PathAction.Deleted || newState.Action != PathAction.Created)
                continue;
            if (!string.Equals(newState.SourceRenameNewFrom, oldPath, StringComparison.OrdinalIgnoreCase))
                continue;

            renameOutputs.Add(new WatcherEventRecord(WatcherEventKind.Renamed, newPath, oldPath));
            state.Remove(oldKey);
            state.Remove(newKey);
        }

        var deletes = state
            .Where(kv => kv.Value.Action == PathAction.Deleted)
            .Select(kv => new WatcherEventRecord(WatcherEventKind.Deleted, kv.Value.OriginalPath, OldPath: null))
            .OrderBy(r => r.Path.Length)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

        var creates = state
            .Where(kv => kv.Value.Action == PathAction.Created)
            .Select(kv => new WatcherEventRecord(WatcherEventKind.Created, kv.Value.OriginalPath, OldPath: null))
            .OrderBy(r => r.Path.Length)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

        return deletes.Concat(renameOutputs).Concat(creates).ToList();
    }

    private static void Upsert(
        Dictionary<string, PathState> state,
        string path,
        PathAction action,
        string? sourceRenameNewFrom)
    {
        var key = Normalize(path);
        state[key] = new PathState
        {
            OriginalPath = path,
            Action = action,
            SourceRenameNewFrom = sourceRenameNewFrom,
        };
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private enum PathAction
    {
        Created,
        Deleted,
    }

    private sealed class PathState
    {
        public string OriginalPath { get; init; } = string.Empty;
        public PathAction Action { get; set; }
        public string? SourceRenameNewFrom { get; set; }
    }

    private readonly record struct RawEvent(WatcherChangeTypes Kind, string Path, string? OldPath);
}
