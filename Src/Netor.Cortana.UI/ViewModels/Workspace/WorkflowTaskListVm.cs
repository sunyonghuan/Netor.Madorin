using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P4 重写：任务列表 ViewModel。
///
/// 数据源：<see cref="TaskExecutionEngine.ListTasksAsync"/>（文件系统持久化）。
/// 增量更新订阅 P4 事件：
/// - <see cref="Events.OnTaskEngineCompleted"/>  → 局部更新 item.Status
/// - <see cref="Events.OnTaskEngineFailed"/>     → 局部更新 item.Status
/// - <see cref="Events.OnTaskEnginePaused"/>     → 局部更新 item.Status
/// - <see cref="Events.OnTaskEngineResumed"/>    → 局部更新 item.Status
/// - <see cref="Events.OnTaskStepCompleted"/>    → 更新 LastActiveTimestamp
/// - <see cref="Events.OnTaskPhaseStarted"/>     → 首次出现时 Insert 新条目
///
/// 订阅永不取消（与 WorkspaceExplorer 模式一致）；Tab 切走后仅 IsVisible=false，
/// 但 VM 仍正常接收事件，下次切回时 UI 已最新。
/// </summary>
public sealed class WorkflowTaskListVm : INotifyPropertyChanged
{
    private readonly TaskExecutionEngine _engine;
    private readonly ISubscriber _subscriber;
    private string _workspaceId = string.Empty;
    private bool _isLoading;
    private WorkflowTaskItemVm? _selectedItem;
    private string? _pendingSelectedTaskId;

    // 阶段 6 Phase 3：任务列表搜索（决策 6-3-A 子串 LIKE 匹配）
    // _keyword 是当前生效的过滤词；_searchDebounceTimer 做 200ms 防抖避免逐字符 hammering DB。
    // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #3。
    private string _keyword = string.Empty;
    private DispatcherTimer? _searchDebounceTimer;

    // P1 群聊真实化：SubMode 过滤（收尾决策 DT-9，2026-05-16 落地）。
    // _subModeFilter null 或空集合 = 不过滤；非空 = 仅显示 SubMode 在该列表中的任务。
    // 由 WorkspaceTabVm.OnAttachedAsync 在 tab 切换时设置：
    // - 「工作流」tab → ["magentic", "parallelanalysis"]
    // - 「群聊」tab → ["groupchat"]
    // 详见 Docs/未来版本策划/界面重设计/05-阶段总结.md §3.1 + §6.2。
    private IReadOnlyList<string>? _subModeFilter;

    /// <summary>构造时立即订阅 workflow 事件流；列表通过 <see cref="LoadAsync"/> 触发首次加载。</summary>
    public WorkflowTaskListVm()
    {
        _engine = App.Services.GetRequiredService<TaskExecutionEngine>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();

        SubscribeEvents();
    }

    /// <summary>任务列表（绑定到 View 层 ItemsControl）。</summary>
    public ObservableCollection<WorkflowTaskItemVm> Items { get; } = [];

    /// <summary>当前选中的任务项（绑定到详情区数据源）。</summary>
    public WorkflowTaskItemVm? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }

    /// <summary>列表是否正在加载（占位 UI）。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>当前过滤的工作区 ID（变化时重新加载列表）。</summary>
    public string WorkspaceId
    {
        get => _workspaceId;
        set
        {
            if (SetField(ref _workspaceId, value))
                _ = LoadAsync();
        }
    }

    /// <summary>
    /// 阶段 6 Phase 3：当前搜索关键词（决策 6-3-A 子串 LIKE 匹配，不引入 FTS5）。
    /// 由 View 层 OnSearchTextChanged → ApplySearch 200ms 防抖后写入；空字符串表示不过滤。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #3。
    /// </summary>
    public string Keyword => _keyword;

    /// <summary>
    /// P1 群聊真实化：当前 SubMode 过滤（收尾决策 DT-9，2026-05-16 落地）。
    /// null 或空集合 = 不过滤；非空 = 仅显示 SubMode 在该列表中的任务。
    /// 切换 SubMode 时立即清空选中项 + 重新加载列表（保证用户切 tab 看到的是干净的 fresh 列表）。
    /// 由 <see cref="WorkspaceTabVm.OnAttachedAsync"/> 在 tab 切换时设置：
    /// - 「工作流」tab → ["magentic", "parallelanalysis"]
    /// - 「群聊」tab → ["groupchat"]
    /// 详见 Docs/未来版本策划/界面重设计/05-阶段总结.md §3.1 + §6.2。
    /// </summary>
    public IReadOnlyList<string>? SubModeFilter
    {
        get => _subModeFilter;
        set
        {
            // 引用比较 + 内容比较（避免传入新数组但内容相同时无谓重载）
            if (ReferenceEquals(_subModeFilter, value)) return;
            if (_subModeFilter is { } a && value is { } b
                && a.Count == b.Count
                && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase))
            {
                _subModeFilter = value;   // 同步引用但不触发 reload
                return;
            }
            _subModeFilter = value;
            // 切换 SubMode 时清空选中项（避免详情区显示其他 tab 的任务）
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// 阶段 6 Phase 3：接收 View 层搜索框文字变化（带 200ms 防抖）。
    /// 在用户停止输入 200ms 后，更新 _keyword 并触发 LoadAsync 重新过滤列表。
    /// </summary>
    public void ApplySearch(string? input)
    {
        var newKeyword = (input ?? string.Empty).Trim();
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            _searchDebounceTimer = null;

            if (string.Equals(newKeyword, _keyword, StringComparison.Ordinal)) return;

            _keyword = newKeyword;
            _ = LoadAsync();
        };
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// 按任务 ID 选中列表项。若任务开始事件尚未插入列表，则暂存选中请求。
    /// </summary>
    public bool SelectTaskById(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return false;

        var item = Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is not null)
        {
            _pendingSelectedTaskId = null;
            SelectedItem = item;
            return true;
        }

        _pendingSelectedTaskId = taskId;
        return false;
    }

    // ──── 列表加载与刷新 ────

    /// <summary>
    /// P4 重写：从 TaskExecutionEngine.ListTasksAsync 加载任务列表。
    /// 决策 8-A：不自动选中历史任务；若调用方明确请求 taskId，则保留该选中项。
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var allTasks = await _engine.ListTasksAsync(CancellationToken.None).ConfigureAwait(false);

            // 客户端过滤（keyword）
            var filtered = allTasks.AsEnumerable();
            if (!string.IsNullOrEmpty(_keyword))
            {
                filtered = filtered.Where(t =>
                    t.Title is not null &&
                    t.Title.Contains(_keyword, StringComparison.OrdinalIgnoreCase));
            }

            var taskList = filtered.Take(30).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedTaskId = _pendingSelectedTaskId ?? _selectedItem?.TaskId;
                Items.Clear();
                foreach (var info in taskList)
                {
                    Items.Add(new WorkflowTaskItemVm(info));
                }

                var selectedItem = string.IsNullOrEmpty(selectedTaskId)
                    ? null
                    : Items.FirstOrDefault(x => x.TaskId == selectedTaskId);
                _pendingSelectedTaskId = selectedItem is null ? _pendingSelectedTaskId : null;
                _selectedItem = selectedItem;
                OnPropertyChanged(nameof(SelectedItem));
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ──── P4 事件订阅 ────

    private void SubscribeEvents()
    {
        // P4: 任务阶段开始（首次出现 requirements 阶段 = 新任务开始）→ Insert 列表项
        _subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseStarted, (_, args) =>
        {
            if (args.Phase != "requirements") return Task.FromResult(false);
            Dispatcher.UIThread.Post(() => OnTaskStarted(args.TaskId));
            return Task.FromResult(false);
        });

        // P4: 任务完成
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTaskCompletedOrFailed(args.TaskId, TaskItemStatus.Completed));
            return Task.FromResult(false);
        });

        // P4: 任务失败（含取消）
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, (_, args) =>
        {
            var status = args.Reason == "cancelled"
                ? TaskItemStatus.Cancelled
                : TaskItemStatus.Failed;
            Dispatcher.UIThread.Post(() => OnTaskCompletedOrFailed(args.TaskId, status));
            return Task.FromResult(false);
        });

        // P4: 任务暂停
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEnginePaused, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTaskStatusChanged(args.TaskId, TaskItemStatus.Paused));
            return Task.FromResult(false);
        });

        // P4: 任务恢复
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineResumed, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTaskStatusChanged(args.TaskId, TaskItemStatus.Running));
            return Task.FromResult(false);
        });

        // P4: 步骤完成 → 更新 LastActiveTimestamp
        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnStepCompleted(args.TaskId));
            return Task.FromResult(false);
        });
    }

    private void OnTaskStarted(string taskId)
    {
        // 已存在则跳过（防重）
        if (Items.Any(x => x.TaskId == taskId)) return;

        var item = new WorkflowTaskItemVm(new AI.TaskEngine.Models.TaskStatusInfo
        {
            TaskId = taskId,
            Status = "running",
            Title = null,
            StartedAt = DateTimeOffset.UtcNow,
        });
        Items.Insert(0, item);

        if (string.Equals(_pendingSelectedTaskId, taskId, StringComparison.Ordinal))
        {
            _pendingSelectedTaskId = null;
            SelectedItem = item;
        }
    }

    private void OnTaskCompletedOrFailed(string taskId, TaskItemStatus status)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;
        item.Status = status;
        item.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void OnTaskStatusChanged(string taskId, TaskItemStatus status)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;
        item.Status = status;
    }

    private void OnStepCompleted(string taskId)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;
        item.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
