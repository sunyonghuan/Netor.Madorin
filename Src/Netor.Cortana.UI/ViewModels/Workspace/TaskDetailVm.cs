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
/// 阶段 3B：Workflow 任务详情的 ViewModel。
///
/// 数据驱动：
/// - <see cref="LoadAsync"/> 从 <see cref="IWorkflowExecutor.GetTaskDetailAsync"/> 拉取完整快照
/// - 订阅 <see cref="Events.OnWorkflowStepCompleted"/> / <see cref="Events.OnWorkflowTaskCompleted"/> /
///   <see cref="Events.OnWorkflowTaskFailed"/>，实时追加步骤 + 更新状态
///
/// View 层根据 <see cref="Status"/> 切换 DataTemplate（Running / Completed / Failed / Cancelled）。
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §5.3 / §5.4。
/// </summary>
public sealed class TaskDetailVm : INotifyPropertyChanged
{
    private readonly IWorkflowExecutor _executor;
    private readonly ISubscriber _subscriber;

    private string _taskId = string.Empty;
    private string _title = string.Empty;
    private string _subMode = string.Empty;
    private WorkflowTaskStatus _status;
    private string? _finalReport;
    private string? _errorMessage;
    private long _startedAt;
    private long? _completedAt;
    private long _totalTokens;   // 阶段 6 Phase 1：累计 token（input + output）
    private bool _isLoading;

    public TaskDetailVm()
    {
        _executor = App.Services.GetRequiredService<IWorkflowExecutor>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        Approval = new WorkflowTaskApprovalVm();
        DynamicAgentCreationApproval = new DynamicAgentCreationApprovalVm();

        SubscribeEvents();
    }

    /// <summary>
    /// HITL 批准卡片 ViewModel（阶段 5B 新增）。
    /// 仅当 Status==Paused 且 RequestId 匹配当前任务时由 OnWorkflowTaskPaused 事件填充并显示。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.1。
    /// </summary>
    public WorkflowTaskApprovalVm Approval { get; }

    /// <summary>
    /// P2-4：动态子智能体创建审批卡片 ViewModel（与 <see cref="Approval"/> 并列，独立显示）。
    /// 仅当 Manager 通过 create_subagent 触发审批且 TaskId 匹配时由 OnDynamicAgentCreationRequested 填充。
    /// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/03-实施阶段.md §4 plan §B.1。
    /// </summary>
    public DynamicAgentCreationApprovalVm DynamicAgentCreationApproval { get; }

    /// <summary>当前展示的任务 ID。空字符串表示无选中。</summary>
    public string TaskId
    {
        get => _taskId;
        private set => SetField(ref _taskId, value);
    }

    /// <summary>任务标题（占位灰色由 View 层处理）。</summary>
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    /// <summary>子模式（groupchat / magentic / parallelanalysis 等）。</summary>
    public string SubMode
    {
        get => _subMode;
        set => SetField(ref _subMode, value);
    }

    /// <summary>当前任务状态。</summary>
    public WorkflowTaskStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsRunning => _status is WorkflowTaskStatus.Running or WorkflowTaskStatus.Pending;
    public bool IsPaused => _status == WorkflowTaskStatus.Paused;
    public bool IsCompleted => _status == WorkflowTaskStatus.Completed;
    public bool IsFailed => _status == WorkflowTaskStatus.Failed;
    public bool IsCancelled => _status == WorkflowTaskStatus.Cancelled;

    public string StatusText => _status switch
    {
        WorkflowTaskStatus.Pending => "等待中",
        WorkflowTaskStatus.Running => "运行中",
        WorkflowTaskStatus.Paused => "已暂停",
        WorkflowTaskStatus.Completed => "已完成",
        WorkflowTaskStatus.Failed => "失败",
        WorkflowTaskStatus.Cancelled => "已取消",
        _ => "未知",
    };

    /// <summary>最终报告（任务完成后展示）。</summary>
    public string? FinalReport
    {
        get => _finalReport;
        set => SetField(ref _finalReport, value);
    }

    /// <summary>错误信息（失败时展示）。</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>任务开始时间（Unix ms）。</summary>
    public long StartedAt
    {
        get => _startedAt;
        set
        {
            if (SetField(ref _startedAt, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>任务完成时间（Unix ms）。</summary>
    public long? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (SetField(ref _completedAt, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>耗时显示文本（"0:08" 或 "1:24"）。</summary>
    public string DurationText
    {
        get
        {
            var endMs = _completedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var seconds = (endMs - _startedAt) / 1000;
            if (seconds < 0) seconds = 0;
            var minutes = seconds / 60;
            var sec = seconds % 60;
            return $"{minutes}:{sec:D2}";
        }
    }

    /// <summary>
    /// 阶段 6 Phase 1：累计 token 总数（input + output），从 OrchestrationTask.TotalTokenCount 或
    /// step 事件中累加得到。详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #2。
    /// </summary>
    public long TotalTokens
    {
        get => _totalTokens;
        set
        {
            if (SetField(ref _totalTokens, value))
                OnPropertyChanged(nameof(TotalTokensText));
        }
    }

    /// <summary>UI 友好的 token 展示文本（如 "12.3k tokens" / "850 tokens"）。空字符串表示无数据。</summary>
    public string TotalTokensText
    {
        get
        {
            if (_totalTokens <= 0) return string.Empty;
            return _totalTokens >= 1000
                ? $"{_totalTokens / 1000.0:F1}k tokens"
                : $"{_totalTokens} tokens";
        }
    }

    /// <summary>是否正在加载详情。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>参与者列表。</summary>
    public ObservableCollection<OrchestrationParticipantEntity> Participants { get; } = [];

    /// <summary>步骤列表（运行期增量追加）。</summary>
    public ObservableCollection<StepItemVm> Steps { get; } = [];

    // ──── 加载与切换 ────

    /// <summary>切换到指定任务的详情。空字符串表示清空。</summary>
    public async Task LoadAsync(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            await Dispatcher.UIThread.InvokeAsync(Clear);
            return;
        }

        IsLoading = true;
        try
        {
            var detail = await _executor.GetTaskDetailAsync(taskId, CancellationToken.None);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (detail is null)
                {
                    Clear();
                    return;
                }
                ApplyDetail(detail);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Clear()
    {
        TaskId = string.Empty;
        Title = string.Empty;
        SubMode = string.Empty;
        Status = WorkflowTaskStatus.Pending;
        FinalReport = null;
        ErrorMessage = null;
        StartedAt = 0;
        CompletedAt = null;
        TotalTokens = 0;
        Participants.Clear();
        Steps.Clear();
        Approval.Clear();
    }

    private void ApplyDetail(OrchestrationTaskDetail detail)
    {
        var task = detail.Task;
        TaskId = task.Id;
        Title = task.Title ?? string.Empty;
        SubMode = task.SubMode;
        Status = WorkflowTaskStatusExtensions.FromDbValue(task.Status);
        FinalReport = task.FinalReport;
        ErrorMessage = task.ErrorMessage;
        StartedAt = task.StartedAt;
        CompletedAt = task.CompletedAt;

        // 阶段 6 Phase 1：从已持久化的 OrchestrationTask.TotalTokenCount 读取（任务完成后写入）；
        // 进行中任务则从 step 集合 SUM 求和（运行期 step 事件订阅会进一步增量更新）
        TotalTokens = task.TotalTokenCount > 0
            ? task.TotalTokenCount
            : detail.Steps.Sum(s => (long)((s.TokenInputCount ?? 0) + (s.TokenOutputCount ?? 0)));

        Participants.Clear();
        foreach (var p in detail.Participants) Participants.Add(p);

        Steps.Clear();
        foreach (var s in detail.Steps) Steps.Add(new WorkflowTimelineStepVm(s));
    }

    // ──── 事件订阅 ────

    private void SubscribeEvents()
    {
        _subscriber.Subscribe<WorkflowStepCompletedArgs>(Events.OnWorkflowStepCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                if (Steps.Any(x => x.StepId == args.StepId)) return;
                Steps.Add(new WorkflowTimelineStepVm(args));
                // 阶段 6 Phase 1：实时增量累加 token，让 UI 在任务运行中持续刷新
                // TokenInputCount / TokenOutputCount 在 args 中为 int?（兼容旧事件），用 ?? 0 安全降级
                TotalTokens += (args.TokenInputCount ?? 0) + (args.TokenOutputCount ?? 0);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskCompletedArgs>(Events.OnWorkflowTaskCompleted, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                Status = WorkflowTaskStatus.Completed;
                FinalReport = args.FinalReport;
                CompletedAt = args.CompletedAt;
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskFailedArgs>(Events.OnWorkflowTaskFailed, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                Status = args.FailureReason == "cancelled"
                    ? WorkflowTaskStatus.Cancelled
                    : WorkflowTaskStatus.Failed;
                ErrorMessage = args.ErrorMessage;
                CompletedAt = args.FailedAt;
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskTitleUpdatedArgs>(Events.OnWorkflowTaskTitleUpdated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                Title = args.NewTitle;
            });
            return Task.FromResult(false);
        });

        // 阶段 5B 新增：HITL 暂停 / 恢复事件订阅（详见 04-实施阶段.md §5B.1）
        _subscriber.Subscribe<WorkflowTaskPausedArgs>(Events.OnWorkflowTaskPaused, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                Status = WorkflowTaskStatus.Paused;
                Approval.Load(args);   // 显示 ApprovalCard
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowTaskResumedArgs>(Events.OnWorkflowTaskResumed, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (args.TaskId != _taskId) return;
                Status = WorkflowTaskStatus.Running;
                Approval.Clear();      // 隐藏 ApprovalCard
            });
            return Task.FromResult(false);
        });

        // P2-4：动态子智能体创建审批事件订阅
        _subscriber.Subscribe<DynamicAgentCreationRequestedArgs>(
            Events.OnDynamicAgentCreationRequested, (_, args) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (args.TaskId != _taskId) return;
                    DynamicAgentCreationApproval.Load(args);
                });
                return Task.FromResult(false);
            });

        _subscriber.Subscribe<DynamicAgentCreationResolvedArgs>(
            Events.OnDynamicAgentCreationResolved, (_, args) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (args.TaskId != _taskId) return;
                    // 用户在本 Detail 实例点了按钮 → ApprovalVm.SubmitAsync 已 Clear；
                    // 这里兜底处理"另一个 Detail 实例响应了"或"任务被外部取消"的场景，
                    // 仅当当前 RequestId 仍是 args.RequestId 时清空（避免误清后续 request）。
                    if (DynamicAgentCreationApproval.RequestId == args.RequestId)
                    {
                        DynamicAgentCreationApproval.Clear();
                    }
                });
                return Task.FromResult(false);
            });
    }

    // ──── 用户操作 ────

    public Task CancelAsync(CancellationToken ct)
        => string.IsNullOrEmpty(_taskId)
            ? Task.CompletedTask
            : _executor.CancelTaskAsync(_taskId, ct);

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
