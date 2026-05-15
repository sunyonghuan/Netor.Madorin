using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.Workflow;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// 阶段 3B：Workflow 任务列表的 ViewModel。
///
/// 订阅以下 5 个 workflow 事件，转换为 <see cref="Items"/>（ObservableCollection）的增量更新，
/// 保证 View 层数据驱动渲染、单点刷新（满足 [06] §5.8.7 验收）：
///
/// - <see cref="Events.OnWorkflowTaskStarted"/>          → Items.Insert(0, item)
/// - <see cref="Events.OnWorkflowTaskCompleted"/>        → 局部更新 item.Status
/// - <see cref="Events.OnWorkflowTaskFailed"/>           → 局部更新 item.Status
/// - <see cref="Events.OnWorkflowTaskTitleUpdated"/>     → 局部更新 item.Title（不重排列表）
/// - <see cref="Events.OnWorkflowStepCompleted"/>        → 更新 LastActiveTimestamp
///
/// 订阅永不取消（与 WorkspaceExplorer 模式一致）；Tab 切走后仅 IsVisible=false，
/// 但 VM 仍正常接收事件，下次切回时 UI 已最新。
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §5.8.4。
/// </summary>
public sealed class WorkflowTaskListVm : INotifyPropertyChanged
{
    private readonly IWorkflowExecutor _executor;
    private readonly ISubscriber _subscriber;
    private string _workspaceId = string.Empty;
    private bool _isLoading;
    private WorkflowTaskItemVm? _selectedItem;

    /// <summary>构造时立即订阅 workflow 事件流；列表通过 <see cref="LoadAsync"/> 触发首次加载。</summary>
    public WorkflowTaskListVm()
    {
        _executor = App.Services.GetRequiredService<IWorkflowExecutor>();
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

    // ──── 列表加载与刷新 ────

    /// <summary>
    /// 重新从数据库加载列表。决策 8-A：启动时不自动选中任何任务（SelectedItem 不复位为旧值）。
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var query = new WorkflowTaskListQuery
            {
                WorkspaceId = _workspaceId,
                IncludeArchived = false,
                Statuses = null,
                Limit = 30,
                Offset = 0,
            };
            var rows = await _executor.ListTasksAsync(query, CancellationToken.None);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Items.Clear();
                foreach (var entity in rows)
                {
                    Items.Add(new WorkflowTaskItemVm(entity));
                }
                // 决策 8-A：启动时不自动选中
                _selectedItem = null;
                OnPropertyChanged(nameof(SelectedItem));
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ──── 事件订阅 ────

    private void SubscribeEvents()
    {
        _subscriber.Subscribe<WorkflowTaskStartedArgs>(Events.OnWorkflowTaskStarted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTaskStarted(args));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskCompletedArgs>(Events.OnWorkflowTaskCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTaskCompletedOrFailed(args.TaskId, WorkflowTaskStatus.Completed));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskFailedArgs>(Events.OnWorkflowTaskFailed, (_, args) =>
        {
            var status = args.FailureReason == "cancelled"
                ? WorkflowTaskStatus.Cancelled
                : WorkflowTaskStatus.Failed;
            Dispatcher.UIThread.Post(() => OnTaskCompletedOrFailed(args.TaskId, status));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskTitleUpdatedArgs>(Events.OnWorkflowTaskTitleUpdated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnTitleUpdated(args));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowStepCompletedArgs>(Events.OnWorkflowStepCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => OnStepCompleted(args));
            return Task.FromResult(false);
        });
    }

    private void OnTaskStarted(WorkflowTaskStartedArgs args)
    {
        // 当前工作区过滤
        if (!IsCurrentWorkspace(args.WorkspaceId)) return;
        // 已存在则跳过（防重）
        if (Items.Any(x => x.TaskId == args.TaskId)) return;

        var entity = new OrchestrationTaskEntity
        {
            Id = args.TaskId,
            Title = args.Title ?? string.Empty,
            IsTitleAutoGenerated = string.IsNullOrEmpty(args.Title),
            Mode = args.Mode,
            SubMode = args.SubMode,
            Status = WorkflowTaskStatus.Running.ToDbValue(),
            StartedAt = args.StartedAt,
            LastActiveTimestamp = args.StartedAt,
            WorkspaceId = args.WorkspaceId,
            TraceId = args.TraceId,
            SourceSessionId = args.SourceSessionId,
            ManagerAgentId = args.ManagerAgentId,
            ManagerAgentName = args.ManagerAgentName,
            InitialInput = args.InitialInput,
        };
        Items.Insert(0, new WorkflowTaskItemVm(entity));
    }

    private void OnTaskCompletedOrFailed(string taskId, WorkflowTaskStatus status)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;
        item.Status = status;
        item.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void OnTitleUpdated(WorkflowTaskTitleUpdatedArgs args)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == args.TaskId);
        if (item is null) return;
        // 决策 [06] §5.8.4：仅 1 处 TextBlock 局部刷新，不重排列表
        item.Title = args.NewTitle;
        item.IsTitleAutoGenerated = args.IsAutoGenerated;
    }

    private void OnStepCompleted(WorkflowStepCompletedArgs args)
    {
        var item = Items.FirstOrDefault(x => x.TaskId == args.TaskId);
        if (item is null) return;
        item.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private bool IsCurrentWorkspace(string workspaceId)
        => string.IsNullOrEmpty(_workspaceId) || _workspaceId == workspaceId;

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
