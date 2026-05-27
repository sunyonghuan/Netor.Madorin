using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// 工作流任务详情 ViewModel（重构版 2026-05-26）。
///
/// 设计原则（文档 08）：
/// - 只有一个 <see cref="FeedItems"/> 对话流，所有信息都是消息
/// - 引擎事件 → 追加不同 Role 的消息到对话流
/// - 没有独立的"审批面板"、"失败详情面板"、"步骤列表面板"
/// - 工具授权请求也是对话流中的一条消息（Role="tool_auth"）
/// </summary>
public sealed class P4TaskDetailVm : INotifyPropertyChanged
{
    private readonly ISubscriber _subscriber;

    private string _taskId = string.Empty;
    private string _taskTitle = string.Empty;
    private string _statusText = "等待中";
    private string _statusColor = "#858585";
    private string _durationText = "0:00";
    private int _msgCounter;
    private DateTimeOffset _startedAt;

    /// <summary>当前活跃步骤 ID（步骤执行期间的 AI 消息将路由到对应卡片）。</summary>
    private string? _activeStepId;

    /// <summary>当前活跃步骤卡片 VM（避免每次查找）。</summary>
    private ConversationMessageVm? _activeStepCard;

    /// <summary>当前活跃阶段卡片（验证阶段等没有 StepId 的 AI 消息路由到此卡片）。</summary>
    private ConversationMessageVm? _activePhaseCard;

    public P4TaskDetailVm()
    {
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        SubscribeEvents();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 属性
    // ══════════════════════════════════════════════════════════════════════

    public string TaskId
    {
        get => _taskId;
        private set => SetField(ref _taskId, value);
    }

    public string Title
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

    // ══════════════════════════════════════════════════════════════════════
    // 兼容属性（GroupChatDetailView 依赖，会议模式未重构前保留）
    // ══════════════════════════════════════════════════════════════════════

    public bool IsPaused    => _statusText == "已暂停";
    public bool IsCompleted => _statusText == "已完成";
    public bool IsRunning   => _statusText == "运行中" || _statusText.StartsWith("执行中");
    public bool IsFailed    => _statusText == "失败";
    public bool IsCancelled => _statusText == "已取消";

    public string? FinalReport   { get; private set; }
    public string? ErrorMessage  { get; private set; }
    public string  TotalTokensText { get; private set; } = string.Empty;

    /// <summary>兼容 GroupChatDetailView 的 Steps 绑定（空集合）。</summary>
    public ObservableCollection<object> Steps { get; } = [];

    // ══════════════════════════════════════════════════════════════════════
    // 统一对话流
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 统一对话流。所有内容（用户消息、AI回复、进度、工具授权、结果）
    /// 都以不同 Role 的消息呈现在这一个列表中。
    /// </summary>
    public ObservableCollection<ConversationMessageVm> FeedItems { get; } = [];

    // ══════════════════════════════════════════════════════════════════════
    // 生命周期
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>同步初始化新任务。initialUserInput 非空时直接写入首条用户消息。</summary>
    public void LoadTask(string taskId, string title, string? initialUserInput = null)
    {
        Clear();
        TaskId = taskId;
        Title = title;
        StatusText = "运行中";
        StatusColor = "#cccccc";
        _startedAt = DateTimeOffset.Now;

        if (!string.IsNullOrWhiteSpace(initialUserInput))
        {
            FeedItems.Add(new ConversationMessageVm
            {
                MessageId = $"msg-init-{taskId}",
                Timestamp = DateTimeOffset.Now,
                Role = "user",
                Content = initialUserInput,
            });
        }
    }

    /// <summary>异步加载已存在任务（列表切换选中时调用）。</summary>
    public async Task LoadAsync(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId)) { Clear(); return; }
        if (taskId == _taskId) return;

        Clear();
        TaskId = taskId;

        var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
        var detail = await engine.GetTaskDetailAsync(taskId, CancellationToken.None).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_taskId != taskId) return; // 用户已切走

            if (detail is null)
            {
                Title = $"任务 {taskId[..Math.Min(8, taskId.Length)]}…";
                StatusText = "未知";
                StatusColor = "#858585";
                return;
            }

            Title = detail.Title;
            _startedAt = detail.CreatedAt;

            (StatusText, StatusColor) = detail.Status switch
            {
                "completed" => ("已完成", "#cccccc"),
                "failed"    => ("失败",   "#cccccc"),
                "paused"    => ("已暂停", "#cccccc"),
                "running"   => ("运行中", "#cccccc"),
                "cancelled" => ("已取消", "#858585"),
                _           => (detail.Status, "#858585"),
            };

            UpdateDuration();
        });
    }

    /// <summary>清空所有状态。</summary>
    public void Clear()
    {
        TaskId = string.Empty;
        Title = string.Empty;
        StatusText = "等待中";
        StatusColor = "#858585";
        DurationText = "0:00";
        _msgCounter = 0;
        FeedItems.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 事件订阅 — 所有引擎事件统一映射为对话流消息
    // ══════════════════════════════════════════════════════════════════════

    private void SubscribeEvents()
    {
        // ── 阶段事件 ──
        // 设计原则：时间线只在用户确认执行后才出现（executing/validating阶段）
        // 需求分析/计划制定阶段保持对话模式，不产生时间线节点

        _subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseStarted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            var name = FormatPhaseName(args.Phase);
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"执行中 — {name}";

                // 需求分析/计划制定阶段：纯对话模式，不产生时间线节点，不创建卡片
                if (args.Phase is "requirements" or "planning")
                    return;

                // 执行阶段：时间线开始，AI 消息靠 StepId 路由到步骤卡片
                if (args.Phase == "executing")
                {
                    AppendTimeline(ProgressKind.PhaseStart, "开始执行", "#cccccc");
                    return;
                }

                // 验证阶段：时间线节点 + 创建卡片收纳验证过程
                AppendTimeline(ProgressKind.PhaseStart, name, "#cccccc");
                var card = new ConversationMessageVm
                {
                    MessageId = $"card-phase-{args.Phase}",
                    Timestamp = DateTimeOffset.Now,
                    Role = "step_card",
                    CardTitle = $"{name}过程",
                    IsExpanded = true,
                    Content = string.Empty,
                };
                FeedItems.Add(card);
                _activePhaseCard = card;
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 需求分析/计划制定阶段完成：不产生时间线节点
                if (args.Phase is "requirements" or "planning")
                    return;

                // 折叠阶段卡片
                if (_activePhaseCard is not null)
                {
                    _activePhaseCard.IsStreaming = false;
                    _activePhaseCard.IsCardCompleted = true;
                    _activePhaseCard.IsExpanded = false;
                    _activePhaseCard = null;
                }

                var name = FormatPhaseName(args.Phase);
                AppendTimeline(ProgressKind.PhaseEnd, $"{name}完成", "#cccccc");
            });
            return Task.FromResult(false);
        });

        // ── 计划事件 ──
        // 计划创建/确认不产生时间线节点（还在对话模式中）

        _subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanCreated, (_, args) =>
        {
            // 不产生时间线节点，计划详情已通过对话消息展示
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanConfirmed, (_, args) =>
        {
            // 不产生时间线节点，执行阶段的 PhaseStarted 会产生时间线起点
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanUpdated, (_, args) =>
        {
            return Task.FromResult(false);
        });

        // ── 步骤事件 → 步骤节点 + 执行卡片 ──

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepStarted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 1) 时间线节点：步骤开始
                AppendTimeline(ProgressKind.StepStart, $"步骤 {args.StepSequence}: {args.Title}", "#cccccc");

                // 2) 创建步骤执行卡片（AI 输出将累积到此卡片的 Content 中）
                var card = new ConversationMessageVm
                {
                    MessageId = $"card-{args.StepId}",
                    Timestamp = DateTimeOffset.Now,
                    Role = "step_card",
                    StepId = args.StepId,
                    CardTitle = $"步骤 {args.StepSequence}: {args.Title}",
                    IsExpanded = true,
                    Content = string.Empty,
                };
                FeedItems.Add(card);

                // 3) 记录活跃步骤
                _activeStepId = args.StepId;
                _activeStepCard = card;
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 折叠步骤卡片
                CompleteStepCard(args.StepId);

                AppendTimeline(ProgressKind.StepEnd,
                    $"步骤 {args.StepSequence} 完成", "#cccccc",
                    subText: args.ResultSummary);
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepFailed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 折叠步骤卡片
                CompleteStepCard(args.StepId);

                AppendTimeline(ProgressKind.StepFail, $"步骤 {args.StepSequence} 失败", "#cccccc");
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepRetryEventArgs>(Events.OnTaskStepRetrying, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
                AppendTimeline(ProgressKind.StepAux, $"步骤 {args.StepSequence} 重试 ({args.RetryCount}/{args.MaxRetries})", "#858585"));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepWaitingUser, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
                AppendTimeline(ProgressKind.StepAux, $"步骤 {args.StepSequence} 等待确认", "#858585"));
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepSkipped, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
                AppendTimeline(ProgressKind.StepAux, $"步骤 {args.StepSequence} 已跳过", "#858585"));
            return Task.FromResult(false);
        });

        // ── 生命周期事件 ──

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 任务总结卡片
                if (!string.IsNullOrWhiteSpace(args.Reason))
                {
                    var summaryCard = new ConversationMessageVm
                    {
                        MessageId = $"card-summary-{_taskId}",
                        Timestamp = DateTimeOffset.Now,
                        Role = "step_card",
                        CardTitle = "任务总结",
                        IsExpanded = true,
                        Content = args.Reason,
                        IsCardCompleted = true,
                    };
                    FeedItems.Add(summaryCard);
                }

                AppendTimeline(ProgressKind.Lifecycle, "✓ 任务已完成", "#cccccc");
                StatusText = "已完成";
                StatusColor = "#cccccc";
                FinalReport = args.Reason;
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            var cancelled = args.Reason == "cancelled";
            Dispatcher.UIThread.Post(() =>
            {
                if (cancelled)
                {
                    AppendTimeline(ProgressKind.Lifecycle, "⏹ 任务已取消", "#cccccc");
                    StatusText = "已取消";
                    StatusColor = "#858585";
                }
                else
                {
                    AppendTimeline(ProgressKind.Lifecycle, $"✗ 任务失败：{args.Reason}", "#cccccc");
                    StatusText = "失败";
                    StatusColor = "#cccccc";
                }
                UpdateDuration();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEnginePaused, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendTimeline(ProgressKind.Lifecycle, "⏸ 任务已暂停", "#cccccc");
                StatusText = "已暂停";
                StatusColor = "#cccccc";
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineResumed, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                AppendTimeline(ProgressKind.Lifecycle, "▶ 任务已恢复", "#cccccc");
                StatusText = "运行中";
                StatusColor = "#cccccc";
            });
            return Task.FromResult(false);
        });

        // ── 验证事件 → 进度消息 ──

        _subscriber.Subscribe<TaskValidationEventArgs>(Events.OnTaskValidationCompleted, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                var title = args.Passed
                    ? $"✓ 验证通过（{args.Score}/100）"
                    : $"✗ 验证未通过（{args.Score}/100）";
                var color = "#cccccc";
                string? sub = null;
                if (args.Issues is { Count: > 0 })
                    sub = string.Join("\n", args.Issues.Select(i => $"└ {i}"));
                AppendTimeline(ProgressKind.Validation, title, color, subText: sub);
            });
            return Task.FromResult(false);
        });

        // ── 对话消息事件（来自 OrchestratorAgent 流式输出） ──
        // 关键改动：带 StepId 的 AI 消息路由到步骤执行卡片，不再作为顶层消息打断时间线

        _subscriber.Subscribe<WorkflowConversationMessageArgs>(Events.OnWorkflowConversationMessage, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 如果有 StepId 且存在对应的步骤卡片 → 累积到卡片内容
                if (!string.IsNullOrEmpty(args.StepId) && TryRouteToStepCard(args.StepId, args.Content))
                    return;

                // 如果有活跃的阶段卡片（验证阶段等）→ 路由到阶段卡片
                if (args.Role == "ai" && _activePhaseCard is not null)
                {
                    RouteToPhaseCard(args.Content);
                    return;
                }

                var existing = FeedItems.FirstOrDefault(m => m.MessageId == args.MessageId);
                if (existing is not null)
                {
                    // 流式追加
                    existing.Content = args.Content;
                    existing.IsStreaming = args.IsStreaming;
                }
                else
                {
                    FeedItems.Add(new ConversationMessageVm
                    {
                        MessageId = args.MessageId,
                        Timestamp = args.OccurredAt,
                        Role = args.Role,
                        Content = args.Content,
                        IsStreaming = args.IsStreaming,
                        ResultSummary = args.ResultSummary,
                        ResultFiles = args.ResultFilePaths?.Select(p => new ResultFileVm
                        {
                            FilePath = p,
                            DisplayName = System.IO.Path.GetFileName(p),
                        }).ToList(),
                    });
                }
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowConversationMessageArgs>(Events.OnWorkflowConversationDelta, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 如果有 StepId 且存在对应的步骤卡片 → 增量追加到卡片内容
                if (!string.IsNullOrEmpty(args.StepId) && TryRouteToStepCard(args.StepId, args.Content, delta: true))
                    return;

                // 如果有活跃的阶段卡片 → 增量路由到阶段卡片
                if (args.Role == "ai" && _activePhaseCard is not null)
                {
                    RouteToPhaseCard(args.Content, delta: true);
                    return;
                }

                var existing = FeedItems.FirstOrDefault(m => m.MessageId == args.MessageId);
                if (existing is not null)
                {
                    existing.Content += args.Content;
                    existing.IsStreaming = args.IsStreaming;
                }
                else
                {
                    FeedItems.Add(new ConversationMessageVm
                    {
                        MessageId = args.MessageId,
                        Timestamp = args.OccurredAt,
                        Role = args.Role,
                        Content = args.Content,
                        IsStreaming = args.IsStreaming,
                    });
                }
            });
            return Task.FromResult(false);
        });

        // ── 危险工具授权事件 → 对话流中的 tool_auth 消息 ──

        _subscriber.Subscribe<WorkflowToolAuthEventArgs>(Events.OnToolAuthorizationRequired, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                FeedItems.Add(new ConversationMessageVm
                {
                    MessageId = $"auth-{args.RequestId}",
                    Timestamp = DateTimeOffset.Now,
                    Role = "tool_auth",
                    Content = args.CallDescription,
                    AuthRequestId = args.RequestId,
                    ToolName = args.ToolName,
                    RiskLevel = args.RiskLevel,
                    ParametersSummary = args.ParametersSummary,
                });
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WorkflowToolAuthEventArgs>(Events.OnToolAuthorizationResolved, (_, args) =>
        {
            if (args.TaskId != _taskId) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                // 授权已处理，追加一条进度消息说明结果
                var decision = args.Decision switch
                {
                    "confirm"   => "已授权",
                    "grant_all" => "已全部授权",
                    "deny"      => "已拒绝",
                    _           => args.Decision ?? "已处理",
                };
                AppendTimeline(ProgressKind.Detail, $"🔒 工具 [{args.ToolName}] {decision}", "#858585");
            });
            return Task.FromResult(false);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部辅助 — 追加不同类型的消息到对话流
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 追加一条时间线进度消息（Role="progress"），带分类和颜色。
    /// 自动维护 IsLastProgress 标记（前一条的竖线 → 当前条截止）。
    /// </summary>
    private void AppendTimeline(ProgressKind kind, string title, string dotColor, string? subText = null)
    {
        // 把前一条 progress 的 IsLastProgress 设为 false（它不再是最后一条）
        for (var i = FeedItems.Count - 1; i >= 0; i--)
        {
            if (FeedItems[i].Role == "progress" && FeedItems[i].IsLastProgress)
            {
                FeedItems[i].IsLastProgress = false;
                break;
            }
        }

        FeedItems.Add(new ConversationMessageVm
        {
            MessageId = $"prog-{Interlocked.Increment(ref _msgCounter):D4}",
            Timestamp = DateTimeOffset.Now,
            Role = "progress",
            Content = title,
            ProgressType = kind,
            DotColor = dotColor,
            TimelineTitle = title,
            TimelineSubText = subText,
            IsLastProgress = true,
        });
    }

    /// <summary>追加一条系统消息（Role="system"）。必须在 UI 线程调用。</summary>
    private void AppendSystem(string text)
    {
        FeedItems.Add(new ConversationMessageVm
        {
            MessageId = $"sys-{Interlocked.Increment(ref _msgCounter):D4}",
            Timestamp = DateTimeOffset.Now,
            Role = "system",
            Content = text,
        });
    }

    private void UpdateDuration()
    {
        var elapsed = DateTimeOffset.Now - _startedAt;
        var total = Math.Max(0, (int)elapsed.TotalSeconds);
        DurationText = $"{total / 60}:{total % 60:D2}";
    }

    private static string FormatPhaseName(string phase) => phase switch
    {
        "requirements" => "需求分析",
        "planning"     => "计划制定",
        "executing"    => "执行",
        "validating"   => "验证",
        _              => phase,
    };

    // ══════════════════════════════════════════════════════════════════════
    // 步骤卡片辅助方法
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 尝试将 AI 消息内容路由到对应 StepId 的步骤卡片。
    /// 如果找到卡片则累积内容并返回 true，否则返回 false（回退为顶层消息）。
    /// </summary>
    private bool TryRouteToStepCard(string stepId, string content, bool delta = false)
    {
        // 优先使用缓存的活跃卡片（热路径）
        var card = _activeStepCard;
        if (card is null || card.StepId != stepId)
        {
            card = FeedItems.FirstOrDefault(m => m.Role == "step_card" && m.StepId == stepId);
            if (card is null) return false;
        }

        if (delta)
            card.Content += content;
        else
            card.Content = (string.IsNullOrEmpty(card.Content) ? "" : card.Content + "\n\n") + content;

        card.IsStreaming = true;
        return true;
    }

    /// <summary>
    /// 步骤完成/失败时：折叠对应卡片、清除活跃步骤标记。
    /// </summary>
    private void CompleteStepCard(string stepId)
    {
        var card = _activeStepCard;
        if (card is null || card.StepId != stepId)
            card = FeedItems.FirstOrDefault(m => m.Role == "step_card" && m.StepId == stepId);

        if (card is not null)
        {
            card.IsStreaming = false;
            card.IsCardCompleted = true;
            card.IsExpanded = false;  // 自动折叠
        }

        if (_activeStepId == stepId)
        {
            _activeStepId = null;
            _activeStepCard = null;
        }
    }

    /// <summary>
    /// 将 AI 消息内容路由到当前活跃的阶段卡片（验证阶段等）。
    /// </summary>
    private void RouteToPhaseCard(string content, bool delta = false)
    {
        var card = _activePhaseCard;
        if (card is null) return;

        if (delta)
            card.Content += content;
        else
            card.Content = (string.IsNullOrEmpty(card.Content) ? "" : card.Content + "\n\n") + content;

        card.IsStreaming = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ══════════════════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
