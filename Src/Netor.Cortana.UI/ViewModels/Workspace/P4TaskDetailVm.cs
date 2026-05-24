using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P4 任务执行引擎的实时详情 ViewModel。
/// 订阅 P4 后端事件（Events.OnTask*），实时构建时间线事件和计划步骤状态。
/// 复用 <see cref="TimelineEventVm"/> / <see cref="PlanStepOverviewVm"/> 数据模型。
///
/// 设计要点（doc 05 §1 无状态决策模型）：
/// - 每个后端事件映射为一条时间线事件
/// - PlanSteps 面板通过 StepSequence 匹配增量更新状态
/// - 头部信息（状态/耗时/token）根据生命周期事件刷新
/// </summary>
public sealed class P4TaskDetailVm : INotifyPropertyChanged
{
    private readonly ISubscriber _subscriber;

    private string _taskId = string.Empty;
    private string _taskTitle = string.Empty;
    private string _statusText = "等待中";
    private string _statusColor = "#858585";
    private string _durationText = "0:00";
    private string _totalTokensText = string.Empty;
    private bool _isPlanOverviewExpanded;
    private int _eventCounter;
    private DateTimeOffset _startedAt;

    public P4TaskDetailVm()
    {
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        SubscribeEvents();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 头部信息（与 P4TimelinePreviewVm 属性名完全一致，XAML 绑定兼容）
    // ══════════════════════════════════════════════════════════════════════

    public string TaskTitle
    {
        get => _taskTitle;
        set => SetField(ref _taskTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public string DurationText
    {
        get => _durationText;
        set => SetField(ref _durationText, value);
    }

    public string TotalTokensText
    {
        get => _totalTokensText;
        set => SetField(ref _totalTokensText, value);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 计划概览面板
    // ══════════════════════════════════════════════════════════════════════

    public bool IsPlanOverviewExpanded
    {
        get => _isPlanOverviewExpanded;
        set
        {
            if (SetField(ref _isPlanOverviewExpanded, value))
                OnPropertyChanged(nameof(PlanToggleIcon));
        }
    }

    public string PlanToggleIcon => _isPlanOverviewExpanded ? "▼" : "▶";

    public ObservableCollection<PlanStepOverviewVm> PlanSteps { get; } = [];

    // ══════════════════════════════════════════════════════════════════════
    // 时间线事件
    // ══════════════════════════════════════════════════════════════════════

    public ObservableCollection<TimelineEventVm> TimelineEvents { get; } = [];

    // ══════════════════════════════════════════════════════════════════════
    // 生命周期
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>初始化为指定任务。</summary>
    public void LoadTask(string taskId, string title)
    {
        Clear();
        _taskId = taskId;
        TaskTitle = title;
        StatusText = "运行中";
        StatusColor = "#007acc";
        _startedAt = DateTimeOffset.Now;

        AppendEvent("task_started", "primary", "任务开始", title, "running");
    }

    /// <summary>清空所有状态（切换任务时）。</summary>
    public void Clear()
    {
        _taskId = string.Empty;
        TaskTitle = string.Empty;
        StatusText = "等待中";
        StatusColor = "#858585";
        DurationText = "0:00";
        TotalTokensText = string.Empty;
        _eventCounter = 0;
        PlanSteps.Clear();
        TimelineEvents.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 事件订阅
    // ══════════════════════════════════════════════════════════════════════

    private void SubscribeEvents()
    {
        // 阶段事件
        _subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseStarted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            var phaseName = FormatPhaseName(args.Phase);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("phase_started", "primary", $"阶段: {phaseName}", null, "running");
                StatusText = $"执行中 — {phaseName}";
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            var phaseName = FormatPhaseName(args.Phase);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("phase_completed", "primary", $"{phaseName} 完成", null, "completed");
            });
            return Task.FromResult(false);
        });

        // 计划事件
        _subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanCreated, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("plan_created", "primary",
                    $"计划已创建（{args.StepCount} 步）", null, "completed");

                // 填充 PlanSteps 占位（后续事件更新状态）
                PlanSteps.Clear();
                for (var i = 1; i <= args.StepCount; i++)
                {
                    PlanSteps.Add(new PlanStepOverviewVm
                    {
                        Sequence = i,
                        Title = $"步骤 {i}",
                        StatusIcon = "⏳",
                        StatusColor = "#858585",
                    });
                }

                IsPlanOverviewExpanded = true;
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanConfirmed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("plan_confirmed", "primary",
                    "计划已确认，准备执行", null, "completed");
            });
            return Task.FromResult(false);
        });

        // 步骤事件
        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepStarted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("step_started", "primary",
                    $"步骤 {args.StepSequence}: {args.Title}", null, "running",
                    args.StepId, args.StepSequence);

                UpdatePlanStep(args.StepSequence, "🔄", "#007acc", args.Title);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepProgressEventArgs>(Events.OnTaskStepProgress, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("step_progress", "secondary",
                    $"[步骤{args.StepSequence}] {args.ProgressDetail ?? $"{args.ProgressPercent}%"}",
                    null, "running", args.StepId, args.StepSequence, args.ProgressPercent);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("step_completed", "primary",
                    $"步骤 {args.StepSequence} 完成: {args.Title}",
                    args.ResultSummary, "completed",
                    args.StepId, args.StepSequence);

                UpdatePlanStep(args.StepSequence, "✅", "#73c991", args.Title);
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepFailed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("step_failed", "primary",
                    $"步骤 {args.StepSequence} 失败: {args.Title}",
                    args.ResultSummary, "failed",
                    args.StepId, args.StepSequence);

                UpdatePlanStep(args.StepSequence, "❌", "#f48771", args.Title);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepRetryEventArgs>(Events.OnTaskStepRetrying, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("step_retrying", "secondary",
                    $"[步骤{args.StepSequence}] 重试 ({args.RetryCount}/{args.MaxRetries})",
                    args.ErrorMessage, "retrying",
                    args.StepId, args.StepSequence);

                UpdatePlanStep(args.StepSequence, "🔄", "#e0c074", args.Title);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepWaitingUser, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("waiting_user", "primary",
                    $"步骤 {args.StepSequence} 等待确认: {args.Title}",
                    null, "waiting",
                    args.StepId, args.StepSequence);

                UpdatePlanStep(args.StepSequence, "⏸", "#e0c074", args.Title);
            });
            return Task.FromResult(false);
        });

        // 生命周期事件
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("task_completed", "primary", "任务完成", args.Reason, "completed");
                StatusText = "已完成";
                StatusColor = "#73c991";
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("task_failed", "primary", "任务失败", args.Reason, "failed");
                StatusText = "失败";
                StatusColor = "#f48771";
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEnginePaused, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("task_paused", "primary", "任务暂停", args.Reason, "waiting");
                StatusText = "已暂停";
                StatusColor = "#e0c074";
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineResumed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendEvent("task_resumed", "primary", "任务恢复", args.Reason, "running");
                StatusText = "运行中";
                StatusColor = "#007acc";
            });
            return Task.FromResult(false);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>追加一条时间线事件。必须在 UI 线程调用。</summary>
    private void AppendEvent(
        string eventType,
        string nodeLevel,
        string title,
        string? detail = null,
        string? status = null,
        string? stepId = null,
        int stepSequence = 0,
        int progressPercent = 0,
        TimelineCardVm? card = null)
    {
        var evt = new TimelineEventVm
        {
            EventId = $"evt-{Interlocked.Increment(ref _eventCounter):D4}",
            Timestamp = DateTimeOffset.Now,
            EventType = eventType,
            NodeLevel = nodeLevel,
            Title = title,
            Detail = detail,
            Status = status,
            StepId = stepId,
            StepSequence = stepSequence > 0 ? stepSequence : null,
            ProgressPercent = progressPercent > 0 ? progressPercent : null,
            Card = card,
        };
        TimelineEvents.Add(evt);
    }

    /// <summary>更新 PlanSteps 面板中指定序号的步骤状态。必须在 UI 线程调用。</summary>
    private void UpdatePlanStep(int sequence, string statusIcon, string statusColor, string? title = null)
    {
        if (sequence <= 0 || sequence > PlanSteps.Count) return;

        var index = sequence - 1;
        var old = PlanSteps[index];

        // PlanStepOverviewVm 是 immutable-like（init 属性），替换整个对象
        PlanSteps[index] = new PlanStepOverviewVm
        {
            Sequence = old.Sequence,
            Title = title ?? old.Title,
            StatusIcon = statusIcon,
            StatusColor = statusColor,
            IsParallel = old.IsParallel,
            DependsOnText = old.DependsOnText,
        };
    }

    /// <summary>更新耗时显示。</summary>
    private void UpdateDuration()
    {
        var elapsed = DateTimeOffset.Now - _startedAt;
        var totalSeconds = (int)elapsed.TotalSeconds;
        if (totalSeconds < 0) totalSeconds = 0;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        DurationText = $"{minutes}:{seconds:D2}";
    }

    /// <summary>格式化阶段名称。</summary>
    private static string FormatPhaseName(string phase) => phase switch
    {
        "requirements" => "需求分析",
        "planning" => "计划制定",
        "executing" => "执行",
        "validation" => "验证",
        _ => phase,
    };

    // ══════════════════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ══════════════════════════════════════════════════════════════════════

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
