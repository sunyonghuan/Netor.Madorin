using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls.Common;
using Netor.EventHub;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式视图控制器，订阅 work.* 事件并驱动 UI 更新。
/// 详见 Docs/已完成功能规划/工作模式方案策划/13-执行引擎与事件驱动.md §6。
/// </summary>
public sealed class WorkModeViewController
{
    private readonly WorkModeView _view;
    private readonly ISubscriber _subscriber;
    private readonly WorkflowExecutor _executor;
    private readonly WorkTaskService _taskService;
    private readonly ICurrentSessionResolver _sessionResolver;

    private TimelineBlock? _currentTimeline;
    private bool _isTimelineAdded;
    private string? _currentMainStepTitle;
    private TimelineStepHandle? _currentMainStep;
    private SubStepCard? _currentSubStep;
    private RealtimeProcessCard? _currentThinkingCard;
    private RealtimeProcessCard? _currentToolCard;
    private AiContent? _currentAssistantContent;
    private bool _isExecutionTimelineActive;
    private readonly List<RealtimeProcessCard> _pendingStepCards = new();
    private readonly Dictionary<string, SubStepCard> _subAgentCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubStepCard> _subAgentJobCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeProcessCard> _subAgentThinkingCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeProcessCard> _subAgentToolCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FoldableCard> _parallelCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubStepCard> _nestedWorkflowCards = new(StringComparer.Ordinal);
    private string? _pendingApprovalTaskId;
    private string? _pendingApprovalRequestId;

    public WorkModeViewController(
        WorkModeView view,
        ISubscriber subscriber,
        WorkflowExecutor executor,
        WorkTaskService taskService,
        ICurrentSessionResolver sessionResolver)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));

        SubscribeEvents();
        _view.ApprovalPopup.DecisionMade += OnApprovalDecisionMade;
        _view.ApprovalPopup.AlternativeSubmitted += OnApprovalAlternativeSubmitted;
        ShowOrphanedTaskPrompt();
    }

    private void SubscribeEvents()
    {
        _subscriber.Subscribe<WorkTaskCreatedArgs>(Events.OnWorkTaskCreated, (_, args) =>
        {
            OnWorkTaskCreated(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkPlanUpdatedArgs>(Events.OnWorkPlanUpdated, (_, args) =>
        {
            OnWorkPlanUpdated(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkAssistantDeltaArgs>(Events.OnWorkAssistantDelta, (_, args) =>
        {
            OnWorkAssistantDelta(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkApprovalRequestedArgs>(Events.OnWorkApprovalRequested, (_, args) =>
        {
            OnWorkApprovalRequested(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkAskUserRequestedArgs>(Events.OnWorkAskUserRequested, (_, args) =>
        {
            OnWorkAskUserRequested(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkTaskCompletedArgs>(Events.OnWorkTaskCompleted, (_, args) =>
        {
            OnWorkTaskCompleted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkTaskFailedArgs>(Events.OnWorkTaskFailed, (_, args) =>
        {
            OnWorkTaskFailed(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkTaskCancelledArgs>(Events.OnWorkTaskCancelled, (_, args) =>
        {
            OnWorkTaskCancelled(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkTaskPausedArgs>(Events.OnWorkTaskPaused, (_, args) =>
        {
            OnWorkTaskPaused(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkStepStartedArgs>(Events.OnWorkStepStarted, (_, args) =>
        {
            OnWorkStepStarted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkStepCompletedArgs>(Events.OnWorkStepCompleted, (_, args) =>
        {
            OnWorkStepCompleted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkToolCallArgs>(Events.OnWorkToolCall, (_, args) =>
        {
            OnWorkToolCall(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkToolResultArgs>(Events.OnWorkToolResult, (_, args) =>
        {
            OnWorkToolResult(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkParallelBlockStartedArgs>(Events.OnWorkParallelBlockStarted, (_, args) =>
        {
            OnWorkParallelBlockStarted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkParallelBlockEndedArgs>(Events.OnWorkParallelBlockEnded, (_, args) =>
        {
            OnWorkParallelBlockEnded(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkNestedWorkflowStartedArgs>(Events.OnWorkNestedWorkflowStarted, (_, args) =>
        {
            OnWorkNestedWorkflowStarted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkNestedWorkflowEndedArgs>(Events.OnWorkNestedWorkflowEnded, (_, args) =>
        {
            OnWorkNestedWorkflowEnded(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkSubAgentJobStartedArgs>(Events.OnWorkSubAgentJobStarted, (_, args) =>
        {
            OnWorkSubAgentJobStarted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkSubAgentJobProgressArgs>(Events.OnWorkSubAgentJobProgress, (_, args) =>
        {
            OnWorkSubAgentJobProgress(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkSubAgentJobCompletedArgs>(Events.OnWorkSubAgentJobCompleted, (_, args) =>
        {
            OnWorkSubAgentJobCompleted(args);
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkSubAgentJobFailedArgs>(Events.OnWorkSubAgentJobFailed, (_, args) =>
        {
            OnWorkSubAgentJobFailed(args);
            return Task.FromResult(false);
        });
    }

    private void OnWorkTaskCreated(WorkTaskCreatedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _view.WelcomePanel.IsVisible = false;
            _currentTimeline = new TimelineBlock();
            _isTimelineAdded = false;
            _currentAssistantContent = null;
            _isExecutionTimelineActive = false;
            _view.RefreshTaskList();
            _view.ScrollToBottom();
        });
    }

    private void ShowOrphanedTaskPrompt()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var sessionId = _sessionResolver.GetCurrentSessionId();
            if (sessionId is null)
            {
                return;
            }

            var activeTask = _taskService.GetActiveTask(sessionId);
            if (activeTask is not { IsOrphaned: true })
            {
                return;
            }

            _view.WelcomePanel.IsVisible = false;
            var card = new TaskResumePromptCard
            {
                TaskId = activeTask.Id,
                TaskTitle = activeTask.Title,
            };
            card.ResumeRequested += OnResumeOrphanedTaskRequested;
            card.CancelRequested += OnCancelOrphanedTaskRequested;
            _view.MessageList.Items.Add(card);
            _view.ScrollToBottom();
        });
    }

    private async void OnResumeOrphanedTaskRequested(string taskId)
    {
        _taskService.ClearOrphaned(taskId);
        _view.MessageList.Items.Add(new AiContent { Markdown = "已恢复未完成任务，我会基于已有上下文继续推进。" });
        _view.ScrollToBottom();
        await _executor.ContinueAsync(taskId, "继续执行上次未完成的任务。");
    }

    private async void OnCancelOrphanedTaskRequested(string taskId)
    {
        await _executor.CancelAsync(taskId);
        _view.MessageList.Items.Add(new AiContent { Markdown = "已放弃上次未完成任务。" });
        _view.RefreshTaskList();
        _view.ScrollToBottom();
    }

    private void OnWorkPlanUpdated(WorkPlanUpdatedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            var ai = new AiContent { Markdown = $"**工作计划已制定**\n\n{FormatPlanMarkdown(args.PlanJson)}" };
            _view.MessageList.Items.Add(ai);
            _view.ScrollToBottom();
        });
    }

    private void OnWorkAssistantDelta(WorkAssistantDeltaArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrEmpty(args.Text)) return;

            if (TryParseSubAgentAuthor(args.AuthorName, out var agentName, out var role))
            {
                var card = GetOrCreateSubAgentCard(agentName);
                if (string.Equals(role, "thinking", StringComparison.Ordinal))
                {
                    var thinkingCard = GetOrCreateSubAgentThinkingCard(agentName, card);
                    thinkingCard.AppendContent(args.Text);
                    _view.ScrollToBottom();
                }
                else
                {
                    CloseSubAgentThinkingCard(agentName);
                    card.AppendDetail(new FoldableCard
                    {
                        CardTitle = "回复",
                        StatusText = "完成",
                        IsExpanded = true,
                        CardContent = new Avalonia.Controls.SelectableTextBlock
                        {
                            Text = args.Text,
                            FontSize = 12,
                            Foreground = Avalonia.Media.Brushes.LightGray,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    });
                }
                return;
            }

            if (string.Equals(args.AuthorName, "thinking", System.StringComparison.Ordinal))
            {
                // 流式思考 → 追加到同一张卡片
                if (_currentThinkingCard is null)
                {
                    CloseCurrentThinkingCard();
                    _currentThinkingCard = new RealtimeProcessCard(new RealtimeProcessEvent
                    {
                        ProcessId = Guid.NewGuid().ToString("N"),
                        Kind = "thinking",
                        Title = "思考",
                        Status = "running",
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    if (_currentSubStep is not null)
                    {
                        ApplyNestedCardMargin(_currentThinkingCard);
                        _currentSubStep.AppendDetail(_currentThinkingCard);
                    }
                    else
                    {
                        // 对话阶段没有当前子步骤，仍按普通消息流显示。
                        ApplyWorkModeCardMargin(_currentThinkingCard);
                        _view.MessageList.Items.Add(_currentThinkingCard);
                    }
                }
                _currentThinkingCard.AppendContent(args.Text);
                _view.ScrollToBottom();
            }
            else
            {
                // 普通文本输出 → 先关闭思考卡片
                CloseCurrentThinkingCard();
                if (_isExecutionTimelineActive)
                {
                    _currentAssistantContent = null;
                    _view.ScrollToBottom();
                    return;
                }

                if (_currentAssistantContent is null)
                {
                    _currentAssistantContent = new AiContent();
                    _view.MessageList.Items.Add(_currentAssistantContent);
                }
                _currentAssistantContent.Markdown = string.Concat(_currentAssistantContent.Markdown, args.Text);
                _view.ScrollToBottom();
            }
        });
    }

    private void CloseCurrentThinkingCard()
    {
        if (_currentThinkingCard is not null)
        {
            _currentThinkingCard.Complete("success", null, 0);
            _currentThinkingCard = null;
        }
    }

    private void OnWorkApprovalRequested(WorkApprovalRequestedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            _view.WelcomePanel.IsVisible = false;
            _pendingApprovalTaskId = args.TaskId;
            _pendingApprovalRequestId = args.RequestId;
            _view.ApprovalPopup.Show(args.ToolName, args.ParameterText, args.Risk);
            _view.ScrollToBottom();
        });
    }

    private void OnWorkAskUserRequested(WorkAskUserRequestedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            _view.WelcomePanel.IsVisible = false;
            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();
            _view.MessageList.Items.Add(new AiContent
            {
                Markdown = $"**需要你确认**\n\n{args.Question}",
            });
            _view.ScrollToBottom();
        });
    }

    private async void OnApprovalDecisionMade(object? sender, ApprovalDecision decision)
    {
        var taskId = _pendingApprovalTaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var response = decision switch
        {
            ApprovalDecision.ApproveOnce => "确认授权：本次允许。",
            ApprovalDecision.ApproveSession => "确认授权：本次会话全部允许。",
            ApprovalDecision.ApproveAlways => "确认授权：永远允许。",
            ApprovalDecision.Reject => "拒绝授权。",
            ApprovalDecision.Alternative => "已提交其他建议。",
            _ => "已处理授权请求。",
        };

        await ResumeFromApprovalAsync(taskId, response);
    }

    private async void OnApprovalAlternativeSubmitted(object? sender, string alternative)
    {
        var taskId = _pendingApprovalTaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        await ResumeFromApprovalAsync(taskId, $"其他建议：{alternative}");
    }

    private async Task ResumeFromApprovalAsync(string taskId, string response)
    {
        _pendingApprovalTaskId = null;
        _pendingApprovalRequestId = null;

        try
        {
            await _executor.ResumeAsync(taskId, response);
        }
        catch (Exception ex)
        {
            _view.MessageList.Items.Add(new AiContent { Markdown = $"**授权响应失败**\n\n{ex.Message}" });
            _view.ScrollToBottom();
        }
    }

    private static void ApplyWorkModeCardMargin(RealtimeProcessCard card)
    {
        // 工作模式：用户气泡 bottom=0，过程卡片 top=2，保持 2px 紧凑间距。
        card.Margin = new Avalonia.Thickness(0, 0, 0, 4);
        var rootBorder = card.FindControl<Avalonia.Controls.Border>("RootBorder");
        if (rootBorder is not null)
        {
            rootBorder.Margin = new Avalonia.Thickness(16, 2, 16, 0);
        }
    }

    private static void ApplyNestedCardMargin(RealtimeProcessCard card)
    {
        card.Margin = new Avalonia.Thickness(0, 0, 0, 4);
        var rootBorder = card.FindControl<Avalonia.Controls.Border>("RootBorder");
        if (rootBorder is not null)
        {
            rootBorder.Margin = new Avalonia.Thickness(0);
        }
    }

    private void OnWorkTaskCompleted(WorkTaskCompletedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();

            if (_currentMainStep is not null)
            {
                _currentMainStep.SetStatus("已完成");
            }

            var summary = new SummaryCard
            {
                SummaryMarkdown = args.FinalReport,
            };
            _view.MessageList.Items.Add(summary);

            var ai = new AiContent { Markdown = "任务已完成。你可以继续提出调整或开始新的任务。" };
            _view.MessageList.Items.Add(ai);
            ResetRuntimeState();
            _view.RefreshTaskList();
            _view.ScrollToBottom();
        });
    }

    private void OnWorkTaskFailed(WorkTaskFailedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();

            var ai = new AiContent { Markdown = $"**任务失败**\n\n{args.ErrorMessage}" };
            _view.MessageList.Items.Add(ai);
            ResetRuntimeState();
            _view.RefreshTaskList();
            _view.ScrollToBottom();
        });
    }

    private void OnWorkTaskCancelled(WorkTaskCancelledArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();

            if (_currentMainStep is not null)
            {
                _currentMainStep.SetStatus("已取消");
            }

            _view.MessageList.Items.Add(new AiContent { Markdown = "任务已取消。" });
            ResetRuntimeState();
            _view.RefreshTaskList();
            _view.ScrollToBottom();
        });
    }

    private void OnWorkTaskPaused(WorkTaskPausedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();

            if (_currentMainStep is not null)
            {
                _currentMainStep.SetStatus("已暂停");
            }

            _view.RefreshTaskList();
            _view.ScrollToBottom();
        });
    }

    private void ResetRuntimeState()
    {
        _currentTimeline = null;
        _isTimelineAdded = false;
        _currentMainStepTitle = null;
        _currentMainStep = null;
        _currentSubStep = null;
        _currentThinkingCard = null;
        _currentToolCard = null;
        _currentAssistantContent = null;
        _isExecutionTimelineActive = false;
        _pendingStepCards.Clear();
        _subAgentCards.Clear();
        _subAgentJobCards.Clear();
        _subAgentThinkingCards.Clear();
        _subAgentToolCards.Clear();
        _parallelCards.Clear();
        _nestedWorkflowCards.Clear();
    }

    private void OnWorkStepStarted(WorkStepStartedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            _isExecutionTimelineActive = true;
            _currentTimeline ??= new TimelineBlock();

            if (!_isTimelineAdded)
            {
                _view.MessageList.Items.Add(_currentTimeline);
                _isTimelineAdded = true;
            }

            var mainTitle = string.IsNullOrWhiteSpace(args.Department) ? args.StepTitle : args.Department;
            if (!string.Equals(_currentMainStepTitle, mainTitle, StringComparison.Ordinal))
            {
                if (_currentMainStep is not null)
                {
                    CloseCurrentThinkingCard();
                    CloseAllSubAgentThinkingCards();
                    _currentMainStep.SetStatus("已完成");
                }

                _currentMainStepTitle = mainTitle;
                _currentMainStep = _currentTimeline.AddMainStep(mainTitle, "运行中", expanded: true);
            }

            _currentSubStep = new SubStepCard
            {
                CardTitle = args.StepTitle,
                StatusText = "运行中",
                IsExpanded = true,
            };
            _currentMainStep?.AppendSubStep(_currentSubStep);
            AttachPendingStepCards();
            _view.ScrollToBottom();
        });
    }

    private void OnWorkStepCompleted(WorkStepCompletedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            if (_currentMainStep is null && _currentSubStep is null) return;

            var sub = _currentSubStep ?? new SubStepCard();
            sub.CardTitle = args.StepTitle;
            sub.StatusText = "已完成";
            sub.IsExpanded = false;

            sub.AppendDetail(new FoldableCard
            {
                CardTitle = "验收",
                StatusText = "通过",
                IsExpanded = false,
                CardContent = new Avalonia.Controls.SelectableTextBlock
                {
                    Text = args.Result,
                    FontSize = 12,
                    Foreground = Avalonia.Media.Brushes.LightGray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                }
            });

            if (_currentSubStep is null)
            {
                _currentMainStep?.AppendSubStep(sub);
                _currentSubStep = sub;
            }

            CloseCurrentThinkingCard();
            CloseAllSubAgentThinkingCards();
            _view.ScrollToBottom();
        });
    }

    private void OnWorkToolCall(WorkToolCallArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            CloseCurrentThinkingCard();

            if (TryParseSubAgentTool(args.ToolName, out var agentName, out var toolName))
            {
                var card = GetOrCreateSubAgentCard(agentName);
                CloseSubAgentThinkingCard(agentName);
                var toolCard = new RealtimeProcessCard(new RealtimeProcessEvent
                {
                    ProcessId = args.CallId,
                    Kind = "agent",
                    Title = toolName,
                    Status = "running",
                    Content = args.ParametersJson ?? string.Empty,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                ApplyNestedCardMargin(toolCard);
                card.AppendDetail(toolCard);
                _subAgentToolCards[args.CallId] = toolCard;
                _view.ScrollToBottom();
                return;
            }

            _currentToolCard = new RealtimeProcessCard(new RealtimeProcessEvent
            {
                ProcessId = args.CallId,
                Kind = "tool",
                Title = args.ToolName,
                Status = "running",
                Content = args.ParametersJson ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow,
            });
            if (_currentSubStep is not null)
            {
                ApplyNestedCardMargin(_currentToolCard);
                _currentSubStep.AppendDetail(_currentToolCard);
            }
            else if (string.Equals(args.ToolName, "dispatch_step", StringComparison.Ordinal))
            {
                ApplyNestedCardMargin(_currentToolCard);
                _pendingStepCards.Add(_currentToolCard);
            }
            else
            {
                ApplyWorkModeCardMargin(_currentToolCard);
                _view.MessageList.Items.Add(_currentToolCard);
            }
            _view.ScrollToBottom();
        });
    }

    private void AttachPendingStepCards()
    {
        if (_currentSubStep is null || _pendingStepCards.Count == 0)
        {
            return;
        }

        foreach (var card in _pendingStepCards)
        {
            _currentSubStep.AppendDetail(card);
        }
        _pendingStepCards.Clear();
    }

    private void OnWorkToolResult(WorkToolResultArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _currentAssistantContent = null;
            if (_subAgentToolCards.Remove(args.CallId, out var subAgentToolCard))
            {
                var content = args.Error ?? args.ResultText ?? "";
                if (!string.IsNullOrEmpty(content))
                    subAgentToolCard.AppendContent(content);
                subAgentToolCard.Complete(args.Status, null, 0);
                _view.ScrollToBottom();
                return;
            }

            if (_currentToolCard is not null)
            {
                var content = args.Error ?? args.ResultText ?? "";
                if (!string.IsNullOrEmpty(content))
                    _currentToolCard.AppendContent(content);
                _currentToolCard.Complete(args.Status, null, 0);
                _currentToolCard = null;
            }

            if (string.Equals(args.Status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args.Error))
            {
                AppendErrorCard("工具错误", args.Error);
            }
            _view.ScrollToBottom();
        });
    }

    private void AppendErrorCard(string title, string error)
    {
        var card = new FoldableCard
        {
            CardTitle = title,
            StatusText = "失败",
            IsExpanded = true,
            CardContent = new Avalonia.Controls.SelectableTextBlock
            {
                Text = error,
                FontSize = 12,
                Foreground = Avalonia.Media.Brushes.LightGray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };

        if (_currentSubStep is not null)
        {
            _currentSubStep.AppendDetail(card);
        }
        else if (_currentMainStep is not null)
        {
            var subStep = new SubStepCard
            {
                CardTitle = title,
                StatusText = "失败",
                IsExpanded = true,
            };
            subStep.AppendDetail(card);
            _currentMainStep.AppendSubStep(subStep);
        }
        else
        {
            _view.MessageList.Items.Add(card);
        }
    }

    private void OnWorkParallelBlockStarted(WorkParallelBlockStartedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var content = args.StepTitles.Length > 0
                ? string.Join(Environment.NewLine, args.StepTitles.Select(title => $"- {title}"))
                : "已创建并行块。";

            var card = new FoldableCard
            {
                CardTitle = "并行执行",
                StatusText = "运行中",
                IsExpanded = true,
                CardContent = new Avalonia.Controls.SelectableTextBlock
                {
                    Text = content,
                    FontSize = 12,
                    Foreground = Avalonia.Media.Brushes.LightGray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            };

            if (_currentSubStep is not null)
            {
                _currentSubStep.AppendDetail(card);
            }
            else if (_currentMainStep is not null)
            {
                var subStep = new SubStepCard
                {
                    CardTitle = "并行子步骤",
                    StatusText = $"{args.StepTitles.Length} 项",
                    IsExpanded = true,
                };
                subStep.AppendDetail(card);
                _currentMainStep.AppendSubStep(subStep);
            }
            else
            {
                _view.MessageList.Items.Add(card);
            }

            _parallelCards[args.BlockId] = card;
            _view.ScrollToBottom();
        });
    }

    private void OnWorkParallelBlockEnded(WorkParallelBlockEndedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_parallelCards.TryGetValue(args.BlockId, out var card))
            {
                card.StatusText = args.SuccessCount == args.TotalCount ? "已完成" : "部分完成";
                card.IsExpanded = args.SuccessCount != args.TotalCount;
                _view.ScrollToBottom();
                return;
            }

            _view.MessageList.Items.Add(new AiContent
            {
                Markdown = $"并行块已结束：{args.SuccessCount}/{args.TotalCount} 个子步骤成功。",
            });
            _view.ScrollToBottom();
        });
    }

    private void OnWorkNestedWorkflowStarted(WorkNestedWorkflowStartedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var card = new SubStepCard
            {
                CardTitle = "嵌套工作流",
                StatusText = "运行中",
                IsExpanded = true,
            };
            card.AppendDetail(new FoldableCard
            {
                CardTitle = "目标",
                StatusText = args.NestedTaskId,
                IsExpanded = true,
                CardContent = new Avalonia.Controls.SelectableTextBlock
                {
                    Text = args.Goal,
                    FontSize = 12,
                    Foreground = Avalonia.Media.Brushes.LightGray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            });

            AttachSubAgentJobCard(card);
            _nestedWorkflowCards[args.NestedTaskId] = card;
            _view.ScrollToBottom();
        });
    }

    private void OnWorkNestedWorkflowEnded(WorkNestedWorkflowEndedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_nestedWorkflowCards.TryGetValue(args.NestedTaskId, out var card))
            {
                card.StatusText = "已完成";
                card.IsExpanded = false;
                card.AppendDetail(new FoldableCard
                {
                    CardTitle = "结果",
                    StatusText = "完成",
                    IsExpanded = false,
                    CardContent = new Avalonia.Controls.SelectableTextBlock
                    {
                        Text = args.FinalReport,
                        FontSize = 12,
                        Foreground = Avalonia.Media.Brushes.LightGray,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                });
                _view.ScrollToBottom();
                return;
            }

            _view.MessageList.Items.Add(new AiContent
            {
                Markdown = $"嵌套工作流已结束：{args.FinalReport}",
            });
            _view.ScrollToBottom();
        });
    }

    private void OnWorkSubAgentJobStarted(WorkSubAgentJobStartedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var card = new SubStepCard
            {
                CardTitle = $"子智能体：{args.AgentName}",
                StatusText = "后台运行中",
                IsExpanded = true,
            };
            card.AppendDetail(new FoldableCard
            {
                CardTitle = "任务",
                StatusText = "已启动",
                IsExpanded = true,
                CardContent = new Avalonia.Controls.SelectableTextBlock
                {
                    Text = args.Query,
                    FontSize = 12,
                    Foreground = Avalonia.Media.Brushes.LightGray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            });

            AttachSubAgentJobCard(card);
            _subAgentJobCards[args.JobId] = card;
            _view.ScrollToBottom();
        });
    }

    private void OnWorkSubAgentJobProgress(WorkSubAgentJobProgressArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_subAgentJobCards.TryGetValue(args.JobId, out var card))
            {
                card.StatusText = args.ProgressDescription;
                _view.ScrollToBottom();
            }
        });
    }

    private void OnWorkSubAgentJobCompleted(WorkSubAgentJobCompletedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_subAgentJobCards.TryGetValue(args.JobId, out var card))
            {
                card.StatusText = "已完成";
                card.IsExpanded = false;
                _view.ScrollToBottom();
            }
        });
    }

    private void OnWorkSubAgentJobFailed(WorkSubAgentJobFailedArgs args)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_subAgentJobCards.TryGetValue(args.JobId, out var card))
            {
                card.StatusText = "失败";
                card.AppendDetail(new FoldableCard
                {
                    CardTitle = "错误",
                    StatusText = "失败",
                    IsExpanded = true,
                    CardContent = new Avalonia.Controls.SelectableTextBlock
                    {
                        Text = args.Error,
                        FontSize = 12,
                        Foreground = Avalonia.Media.Brushes.LightGray,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                });
                _view.ScrollToBottom();
            }
        });
    }

    private void AttachSubAgentJobCard(SubStepCard card)
    {
        if (_currentSubStep is not null)
        {
            _currentSubStep.AppendDetail(card);
        }
        else if (_currentMainStep is not null)
        {
            _currentMainStep.AppendSubStep(card);
        }
        else
        {
            _view.MessageList.Items.Add(card);
        }
    }

    private static string FormatPlanMarkdown(string planJson)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(planJson);
            var sb = new System.Text.StringBuilder();
            var root = doc.RootElement;

            if (root.TryGetProperty("MainSteps", out var steps) || root.TryGetProperty("main_steps", out steps))
            {
                var i = 0;
                foreach (var step in steps.EnumerateArray())
                {
                    i++;
                    var title = step.TryGetProperty("Title", out var t) ? t.GetString()
                        : step.TryGetProperty("title", out t) ? t.GetString() : $"步骤 {i}";
                    sb.AppendLine($"**{i}. {title}**");

                    if (step.TryGetProperty("SubSteps", out var subs) || step.TryGetProperty("sub_steps", out subs))
                    {
                        foreach (var sub in subs.EnumerateArray())
                        {
                            var subTitle = sub.TryGetProperty("Title", out var st) ? st.GetString()
                                : sub.TryGetProperty("title", out st) ? st.GetString() : "子步骤";
                            sb.AppendLine($"   - {subTitle}");
                        }
                    }
                    sb.AppendLine();
                }
            }

            return sb.Length > 0 ? sb.ToString() : planJson;
        }
        catch
        {
            return planJson;
        }
    }

    private SubStepCard GetOrCreateSubAgentCard(string agentName)
    {
        if (_subAgentCards.TryGetValue(agentName, out var card))
        {
            return card;
        }

        card = new SubStepCard
        {
            CardTitle = $"子智能体：{agentName}",
            StatusText = "运行中",
            IsExpanded = true,
        };

        if (_currentSubStep is not null)
        {
            _currentSubStep.AppendDetail(card);
        }
        else if (_currentMainStep is not null)
        {
            _currentMainStep.AppendSubStep(card);
        }
        else
        {
            _view.MessageList.Items.Add(card);
        }
        _view.ScrollToBottom();

        _subAgentCards[agentName] = card;
        return card;
    }

    private RealtimeProcessCard GetOrCreateSubAgentThinkingCard(string agentName, SubStepCard ownerCard)
    {
        if (_subAgentThinkingCards.TryGetValue(agentName, out var existing))
        {
            return existing;
        }

        var card = new RealtimeProcessCard(new RealtimeProcessEvent
        {
            ProcessId = $"{agentName}:thinking:{Guid.NewGuid():N}",
            Kind = "thinking",
            Title = "思考",
            Status = "running",
            Timestamp = DateTimeOffset.UtcNow,
        });
        ApplyNestedCardMargin(card);
        ownerCard.AppendDetail(card);
        _subAgentThinkingCards[agentName] = card;
        return card;
    }

    private void CloseSubAgentThinkingCard(string agentName)
    {
        if (_subAgentThinkingCards.Remove(agentName, out var card))
        {
            card.Complete("success", null, 0);
        }
    }

    private void CloseAllSubAgentThinkingCards()
    {
        foreach (var card in _subAgentThinkingCards.Values)
        {
            card.Complete("success", null, 0);
        }

        _subAgentThinkingCards.Clear();
    }

    private static bool TryParseSubAgentAuthor(string? authorName, out string agentName, out string role)
    {
        agentName = string.Empty;
        role = string.Empty;
        if (string.IsNullOrWhiteSpace(authorName))
        {
            return false;
        }

        var separator = authorName.IndexOf(':');
        if (separator <= 0 || separator == authorName.Length - 1)
        {
            return false;
        }

        agentName = authorName[..separator];
        role = authorName[(separator + 1)..];
        return !string.Equals(agentName, "thinking", StringComparison.Ordinal);
    }

    private static bool TryParseSubAgentTool(string? toolName, out string agentName, out string innerToolName)
    {
        agentName = string.Empty;
        innerToolName = string.Empty;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var separator = toolName.IndexOf('.');
        if (separator <= 0 || separator == toolName.Length - 1)
        {
            return false;
        }

        agentName = toolName[..separator];
        innerToolName = toolName[(separator + 1)..];
        return true;
    }

}
