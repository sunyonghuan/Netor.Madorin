using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.ExpertMode;
using Netor.Cortana.UI.ViewModels.WorkModeVm;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式主视图（WorkModeView）。
///
/// 阶段 1（仅 UI 预览）：
/// - 复用 InputAreaView（橙色 #FF8C00 走马灯）
/// - 提供 LoadDemoTimeline() 静态填充演示数据，便于验证视觉效果
/// - 不接业务逻辑（AF 框架接入留待阶段 2）
/// </summary>
public partial class WorkModeView : UserControl
{
    private WorkModeViewController? _controller;
    private WorkTaskService? _taskService;
    private WorkExecutionLogService? _logService;
    private WorkTaskFileService? _fileService;
    private WorkTaskContextService? _contextService;
    private Netor.Cortana.AI.WorkMode.ICurrentSessionResolver? _sessionResolver;
    private WorkModeInputVm? _inputVm;
    private bool _userScrolledUp;

    public WorkModeView()
    {
        InitializeComponent();
        WelcomeLogoImage.Source = LoadBrandLogoBitmap();

        // 工作模式使用独立的 WorkModeInputVm
        var inputVm = App.Services.GetService<WorkModeInputVm>();
        if (inputVm is not null)
        {
            _inputVm = inputVm;
            WorkInputArea.SetInputVm(inputVm);
            inputVm.UserMessageSubmitted += text =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    WelcomePanel.IsVisible = false;
                    var bubble = new UserBubble { Text = text };
                    MessageList.Items.Add(bubble);
                    ScrollToBottom();
                });
            };
        }
        else
        {
            // 回退：使用 ChatInputVm（DI 未注册 WorkModeInputVm 时）
            var chatInputVm = App.Services.GetRequiredService<ChatInputVm>();
            WorkInputArea.SetInputVm(chatInputVm);
        }

        AttachedToVisualTree += (_, _) =>
        {
            // 演示代码晚清理原则：注释掉调用入口，保留方法体
            // LoadDemoConversation();

            // 接入 WorkModeViewController（手动构建，因为需要传入 this）
            if (_controller is null)
            {
                var subscriber = App.Services.GetService<Netor.EventHub.ISubscriber>();
                var executor = App.Services.GetService<Netor.Cortana.AI.WorkMode.WorkflowExecutor>();
                _taskService = App.Services.GetService<WorkTaskService>();
                _logService = App.Services.GetService<WorkExecutionLogService>();
                _fileService = App.Services.GetService<WorkTaskFileService>();
                _contextService = App.Services.GetService<WorkTaskContextService>();
                _sessionResolver = App.Services.GetService<Netor.Cortana.AI.WorkMode.ICurrentSessionResolver>();
                if (subscriber is not null && executor is not null && _taskService is not null && _sessionResolver is not null)
                {
                    _controller = new WorkModeViewController(this, subscriber, executor, _taskService, _sessionResolver);
                }
            }
        };

        // 授权浮窗的真实响应由 WorkModeViewController 接入 WorkflowExecutor。
    }

    /// <summary>
    /// 从品牌默认入口加载欢迎页 Logo。
    /// </summary>
    private static Bitmap LoadBrandLogoBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.LogoImageFileName)));
        return new Bitmap(stream);
    }

    // ──── 演示数据填充 ────

    internal void RefreshTaskList()
    {
        // 工作任务列表已迁移到 LeftPanel 的“工作记录”页；保留方法供旧事件调用不再操作 UI。
    }

    internal void StartNewWork()
    {
        MessageList.Items.Clear();
        ApprovalPopup.IsVisible = false;
        WelcomePanel.IsVisible = true;
        _userScrolledUp = false;
        ScrollToBottomBtn.IsVisible = false;
        _inputVm?.PrepareNewTask();
    }

    internal void ShowNotice(string message, string title, string severity)
    {
        WelcomePanel.IsVisible = false;
        MessageList.Items.Add(new AiContent
        {
            Markdown = $"**{title}**\n\n{message}",
        });
        ScrollToBottom();
    }

    internal void LoadTaskHistory(string taskId)
    {
        _taskService ??= App.Services.GetService<WorkTaskService>();
        _logService ??= App.Services.GetService<WorkExecutionLogService>();
        _fileService ??= App.Services.GetService<WorkTaskFileService>();
        _contextService ??= App.Services.GetService<WorkTaskContextService>();

        if (_taskService is null)
        {
            return;
        }

        var task = _taskService.GetById(taskId);
        if (task is null)
        {
            return;
        }

        WelcomePanel.IsVisible = false;
        MessageList.Items.Clear();
        _inputVm?.SetSelectedTask(task.Id);

        var planStatus = _fileService?.LoadPlan(task.Id)?.Status;
        var restoredMessages = RestoreConversationMessages(task);

        if (restoredMessages == 0)
        {
            MessageList.Items.Add(new AiContent
            {
                Markdown = BuildTaskOverviewMarkdown(task, planStatus),
            });
        }

        if (!string.IsNullOrWhiteSpace(task.CurrentPlanJson))
        {
            MessageList.Items.Add(new AiContent
            {
                Markdown = $"**任务计划**\n\n{FormatPlanMarkdown(task.CurrentPlanJson)}",
            });
        }

        var logs = _logService?.ListByTask(task.Id) ?? [];
        if (logs.Count > 0)
        {
            var timeline = BuildTimelineFromLogs(logs, task, planStatus);
            if (timeline is not null)
            {
                MessageList.Items.Add(timeline);
            }
            else
            {
                MessageList.Items.Add(new AiContent
                {
                    Markdown = BuildLogSummaryMarkdown(logs),
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(task.FinalReport))
        {
            MessageList.Items.Add(new SummaryCard
            {
                SummaryMarkdown = task.FinalReport,
            });
        }

        ScrollToBottom();
    }

    private int RestoreConversationMessages(WorkTaskEntity task)
    {
        var runId = task.RunId;
        if (string.IsNullOrWhiteSpace(runId) || _contextService is null)
        {
            return 0;
        }

        var messages = _contextService.ListMessages(task.Id, runId);
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                MessageList.Items.Add(new UserBubble { Text = message.Content });
            }
            else if (string.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                MessageList.Items.Add(new AiContent { Markdown = message.Content });
            }
        }

        return messages.Count;
    }

    private static string BuildTaskOverviewMarkdown(WorkTaskEntity task, string? planStatus)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{task.Title}**");
        sb.AppendLine();
        sb.AppendLine($"- 工作记录：{(task.IsActive ? "可继续" : "已关闭")}");
        sb.AppendLine($"- 当前执行：{FormatTaskStatus(task, planStatus)}");
        sb.AppendLine($"- 初始输入：{task.InitialInput}");
        if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
        {
            sb.AppendLine($"- 最近结束原因：{task.ErrorMessage}");
        }
        if (!string.IsNullOrWhiteSpace(task.SourceTaskId))
        {
            sb.AppendLine($"- 来源任务：{task.SourceTaskId}");
        }
        return sb.ToString();
    }

    private static string FormatTaskStatus(WorkTaskEntity task, string? planStatus)
    {
        if (!task.IsActive)
        {
            return "已关闭";
        }

        return task.OrchestratorState switch
        {
            WorkTaskOrchestratorStates.Running => "执行中",
            WorkTaskOrchestratorStates.Paused => "已暂停",
            _ => planStatus switch
            {
                WorkTaskPlanStatuses.Planning => "待执行计划",
                WorkTaskPlanStatuses.Running => "执行中",
                WorkTaskPlanStatuses.Paused => "已暂停",
                WorkTaskPlanStatuses.Done => "本轮已完成",
                WorkTaskPlanStatuses.Failed => "本轮失败",
                WorkTaskPlanStatuses.Cancelled => "本轮已取消",
                _ => "等待讨论或计划",
            },
        };
    }

    private static string BuildLogSummaryMarkdown(IReadOnlyList<WorkExecutionLogEntity> logs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**执行日志摘要**");
        sb.AppendLine();

        foreach (var log in logs.TakeLast(30))
        {
            sb.AppendLine($"- #{log.Sequence} {FormatLogLine(log)}");
        }

        return sb.ToString();
    }

    private static string FormatLogLine(WorkExecutionLogEntity log)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(log.Content);
            var root = doc.RootElement;
            return log.LogType switch
            {
                WorkExecutionLogTypes.StepStart => TryGetProperty(root, "StepTitle", "stepTitle", out var title)
                    ? $"开始步骤：{title.GetString()}"
                    : log.LogType,
                WorkExecutionLogTypes.Acceptance => TryGetProperty(root, "Verdict", "verdict", out var verdict)
                    ? $"验收：{verdict.GetString()}"
                    : log.LogType,
                WorkExecutionLogTypes.ToolCall => TryGetProperty(root, "ToolName", "toolName", out var tool)
                    ? $"调用工具：{tool.GetString()}"
                    : log.LogType,
                WorkExecutionLogTypes.ToolResult => TryGetProperty(root, "Status", "status", out var status)
                    ? FormatToolResult(root, status.GetString())
                    : log.LogType,
                WorkExecutionLogTypes.DeadlockDetected => TryGetProperty(root, "Reason", "reason", out var reason)
                    ? $"死循环风险：{reason.GetString()}"
                    : log.LogType,
                WorkExecutionLogTypes.SelfEvaluation => TryGetProperty(root, "Score", "score", out var score)
                    ? $"自评：{score.GetInt32()}/10"
                    : log.LogType,
                _ => log.LogType,
            };
        }
        catch
        {
            return log.LogType;
        }
    }

    private static string FormatToolResult(System.Text.Json.JsonElement root, string? status)
    {
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            && TryGetProperty(root, "Error", "error", out var error)
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            return $"工具结果：失败 - {error.GetString()}";
        }

        return $"工具结果：{status}";
    }

    private static TimelineBlock? BuildTimelineFromLogs(IReadOnlyList<WorkExecutionLogEntity> logs, WorkTaskEntity task, string? planStatus)
    {
        var timeline = new TimelineBlock();
        var mainSteps = new Dictionary<string, TimelineStepHandle>(StringComparer.Ordinal);
        var subSteps = new Dictionary<string, SubStepCard>(StringComparer.Ordinal);
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var thinkingCards = new Dictionary<SubStepCard, FoldableCard>();

        TimelineStepHandle? currentMain = null;
        SubStepCard? currentSub = null;
        string? currentMainTitle = null;

        foreach (var log in logs)
        {
            if (!TryParseLog(log, out var root))
            {
                continue;
            }

            switch (log.LogType)
            {
                case WorkExecutionLogTypes.StepStart:
                {
                    var stepTitle = ReadString(root, "StepTitle", "stepTitle") ?? "子步骤";
                    var mainTitle = ReadString(root, "Department", "department") ?? stepTitle;
                    currentMain = GetOrCreateMainStep(timeline, mainSteps, mainTitle, ref currentMainTitle);
                    currentSub = new SubStepCard
                    {
                        CardTitle = stepTitle,
                        StatusText = IsTaskExecutionRunning(task, planStatus) ? "运行中" : "已完成",
                        IsExpanded = IsTaskExecutionRunning(task, planStatus),
                    };

                    var context = ReadString(root, "Context", "context");
                    if (!string.IsNullOrWhiteSpace(context))
                    {
                        currentSub.AppendDetail(CreateFoldableCard("执行说明", context, null, expanded: false));
                    }

                    currentMain.AppendSubStep(currentSub);
                    subSteps[stepTitle] = currentSub;
                    break;
                }
                case WorkExecutionLogTypes.StepComplete:
                {
                    var stepTitle = ReadString(root, "StepTitle", "stepTitle") ?? currentSub?.CardTitle ?? "子步骤";
                    var result = ReadString(root, "Result", "result") ?? "步骤已完成。";
                    var targetSub = subSteps.TryGetValue(stepTitle, out var storedSub) ? storedSub : currentSub;
                    if (targetSub is null)
                    {
                        AppendToCurrentSubStep(
                            timeline,
                            ref currentMain,
                            ref currentSub,
                            ref currentMainTitle,
                            mainSteps,
                            subSteps,
                            CreateFoldableCard("步骤完成", result, "完成", expanded: false));
                    }
                    else
                    {
                        targetSub.StatusText = "已完成";
                        targetSub.IsExpanded = false;
                        targetSub.AppendDetail(CreateFoldableCard("验收", result, "通过", expanded: false));
                    }
                    break;
                }
                case WorkExecutionLogTypes.Thinking:
                {
                    var authorName = ReadString(root, "AuthorName", "authorName");
                    var text = ReadString(root, "Text", "text") ?? string.Empty;
                    var title = string.IsNullOrWhiteSpace(authorName) ? "思考" : $"{authorName} 思考";
                    AppendThinkingToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        thinkingCards,
                        title,
                        text);
                    break;
                }
                case WorkExecutionLogTypes.ToolCall:
                {
                    var callId = ReadString(root, "CallId", "callId") ?? $"call-{log.Sequence}";
                    var toolName = ReadString(root, "ToolName", "toolName") ?? "工具";
                    toolNames[callId] = toolName;

                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"调用工具 {toolName}", "参数已省略，保留工具调用记录。", "完成", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.ToolResult:
                {
                    var callId = ReadString(root, "CallId", "callId") ?? string.Empty;
                    var status = ReadString(root, "Status", "status") ?? "unknown";
                    var error = ReadString(root, "Error", "error");
                    var toolName = toolNames.TryGetValue(callId, out var storedToolName) ? storedToolName : "工具";
                    var failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
                    var content = failed
                        ? error ?? "工具执行失败。"
                        : "工具结果正文已省略，保留成功/失败状态和步骤标题。";

                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard(
                            failed ? $"{toolName} 错误" : $"{toolName} 结果",
                            content,
                            failed ? "失败" : "完成",
                            expanded: failed));
                    break;
                }
                case WorkExecutionLogTypes.Acceptance:
                {
                    var stepTitle = ReadString(root, "StepTitle", "stepTitle") ?? currentSub?.CardTitle ?? "验收";
                    var verdict = ReadString(root, "Verdict", "verdict") ?? string.Empty;
                    var reason = ReadString(root, "Reason", "reason") ?? "无验收说明。";
                    var targetSub = subSteps.TryGetValue(stepTitle, out var storedSub) ? storedSub : currentSub;
                    var statusText = verdict switch
                    {
                        "PASS" => "通过",
                        "FAIL" => "未通过",
                        "SEND_BACK" => "退回",
                        _ => verdict,
                    };

                    if (targetSub is null)
                    {
                        AppendToCurrentSubStep(
                            timeline,
                            ref currentMain,
                            ref currentSub,
                            ref currentMainTitle,
                            mainSteps,
                            subSteps,
                            CreateFoldableCard("验收", reason, statusText, expanded: verdict != "PASS"));
                    }
                    else
                    {
                        targetSub.StatusText = verdict == "PASS" ? "已完成" : "需处理";
                        targetSub.IsExpanded = verdict != "PASS";
                        targetSub.AppendDetail(CreateFoldableCard("验收", reason, statusText, expanded: verdict != "PASS"));
                    }
                    break;
                }
                case WorkExecutionLogTypes.SelfEvaluation:
                {
                    var stepTitle = ReadString(root, "StepTitle", "stepTitle") ?? "自评";
                    var score = ReadInt(root, "Score", "score");
                    var verdict = ReadString(root, "Verdict", "verdict") ?? string.Empty;
                    var issues = ReadStringArray(root, "Issues", "issues");
                    var content = issues.Count == 0
                        ? verdict
                        : $"{verdict}{Environment.NewLine}{string.Join(Environment.NewLine, issues.Select(x => $"- {x}"))}";

                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"{stepTitle} 自评", content, score is null ? null : $"{score}/10", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.DeadlockDetected:
                {
                    var reason = ReadString(root, "Reason", "reason") ?? "检测到重复执行风险。";
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard("执行风险", reason, "需调整", expanded: true));
                    break;
                }
                case WorkExecutionLogTypes.ParallelBlockStart:
                {
                    var blockId = ReadString(root, "BlockId", "blockId") ?? "parallel";
                    var stepTitles = ReadStringArray(root, "StepTitles", "stepTitles");
                    var content = stepTitles.Count == 0
                        ? "并行块已启动。"
                        : string.Join(Environment.NewLine, stepTitles.Select(x => $"- {x}"));
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"并行执行 {blockId}", content, "运行中", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.ParallelBlockEnd:
                {
                    var blockId = ReadString(root, "BlockId", "blockId") ?? "parallel";
                    var total = ReadInt(root, "TotalCount", "totalCount") ?? 0;
                    var success = ReadInt(root, "SuccessCount", "successCount") ?? 0;
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"并行完成 {blockId}", $"成功 {success}/{total}", success == total ? "完成" : "部分完成", expanded: success != total));
                    break;
                }
                case WorkExecutionLogTypes.NestedWorkflowStart:
                {
                    var nestedTaskId = ReadString(root, "NestedTaskId", "nestedTaskId") ?? "nested";
                    var goal = ReadString(root, "Goal", "goal") ?? "嵌套子工作流已启动。";
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"嵌套工作流 {nestedTaskId}", goal, "运行中", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.NestedWorkflowEnd:
                {
                    var nestedTaskId = ReadString(root, "NestedTaskId", "nestedTaskId") ?? "nested";
                    var finalReport = ReadString(root, "FinalReport", "finalReport") ?? "嵌套子工作流已结束。";
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"嵌套完成 {nestedTaskId}", finalReport, "完成", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.SubAgentJobStart:
                {
                    var ownerName = ReadString(root, "OwnerName", "ownerName") ?? "子智能体";
                    var query = ReadString(root, "Query", "query") ?? "后台任务已启动。";
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard($"子智能体：{ownerName}", query, "后台运行", expanded: false));
                    break;
                }
                case WorkExecutionLogTypes.SubAgentJobEnd:
                {
                    var status = ReadString(root, "Status", "status") ?? "完成";
                    var result = ReadString(root, "ResultJson", "resultJson");
                    var error = ReadString(root, "Error", "error");
                    AppendToCurrentSubStep(
                        timeline,
                        ref currentMain,
                        ref currentSub,
                        ref currentMainTitle,
                        mainSteps,
                        subSteps,
                        CreateFoldableCard("子智能体结果", error ?? result ?? "后台任务已结束。", status, expanded: !string.IsNullOrWhiteSpace(error)));
                    break;
                }
            }
        }

        if (timeline.StepCount == 0)
        {
            return null;
        }

        foreach (var step in mainSteps.Values)
        {
            step.SetExpanded(true);
            step.SetStatus(IsTaskExecutionRunning(task, planStatus) ? "运行中" : "已完成");
        }

        if (string.Equals(planStatus, WorkTaskPlanStatuses.Failed, StringComparison.Ordinal)
            || string.Equals(planStatus, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal))
        {
            currentMain?.SetStatus("已结束");
        }

        return timeline;
    }

    private static bool IsTaskExecutionRunning(WorkTaskEntity task, string? planStatus)
        => string.Equals(task.OrchestratorState, WorkTaskOrchestratorStates.Running, StringComparison.Ordinal);

    private static TimelineStepHandle GetOrCreateMainStep(
        TimelineBlock timeline,
        Dictionary<string, TimelineStepHandle> mainSteps,
        string mainTitle,
        ref string? currentMainTitle)
    {
        if (mainSteps.TryGetValue(mainTitle, out var existing))
        {
            currentMainTitle = mainTitle;
            return existing;
        }

        if (currentMainTitle is not null && mainSteps.TryGetValue(currentMainTitle, out var previous))
        {
            previous.SetStatus("已完成");
        }

        var created = timeline.AddMainStep(mainTitle, "已完成", expanded: true);
        mainSteps[mainTitle] = created;
        currentMainTitle = mainTitle;
        return created;
    }

    private static void AppendToCurrentSubStep(
        TimelineBlock timeline,
        ref TimelineStepHandle? currentMain,
        ref SubStepCard? currentSub,
        ref string? currentMainTitle,
        Dictionary<string, TimelineStepHandle> mainSteps,
        Dictionary<string, SubStepCard> subSteps,
        Control card)
    {
        if (currentSub is null)
        {
            // 规划阶段的工具调用也会写入 WorkExecutionLogs。没有 StepStart 时不能造出
            // “执行过程”阶段，否则会破坏“主阶段 -> 子步骤 -> 过程”的回放结构。
            return;
        }

        currentSub.AppendDetail(card);
    }

    private static void AppendThinkingToCurrentSubStep(
        TimelineBlock timeline,
        ref TimelineStepHandle? currentMain,
        ref SubStepCard? currentSub,
        ref string? currentMainTitle,
        Dictionary<string, TimelineStepHandle> mainSteps,
        Dictionary<string, SubStepCard> subSteps,
        Dictionary<SubStepCard, FoldableCard> thinkingCards,
        string title,
        string text)
    {
        if (currentSub is null)
        {
            return;
        }

        if (!thinkingCards.TryGetValue(currentSub, out var card))
        {
            card = CreateFoldableCard(title, text, "完成", expanded: false);
            thinkingCards[currentSub] = card;
            AppendToCurrentSubStep(
                timeline,
                ref currentMain,
                ref currentSub,
                ref currentMainTitle,
                mainSteps,
                subSteps,
                card);
            return;
        }

        var existing = card.CardContent is Avalonia.Controls.SelectableTextBlock block
            ? block.Text ?? string.Empty
            : string.Empty;
        card.CardContent = MakeContentBlock(string.Concat(existing, text));
    }

    private static FoldableCard CreateFoldableCard(string title, string content, string? statusText, bool expanded)
    {
        return new FoldableCard
        {
            CardTitle = title,
            StatusText = statusText,
            IsExpanded = expanded,
            CardContent = MakeContentBlock(content),
        };
    }

    private static bool TryParseLog(WorkExecutionLogEntity log, out System.Text.Json.JsonElement root)
    {
        root = default;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(log.Content);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string clrName, string jsonName)
    {
        return TryGetProperty(element, clrName, jsonName, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    private static int? ReadInt(System.Text.Json.JsonElement element, string clrName, string jsonName)
    {
        if (!TryGetProperty(element, clrName, jsonName, out var value))
        {
            return null;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static List<string> ReadStringArray(System.Text.Json.JsonElement element, string clrName, string jsonName)
    {
        if (!TryGetProperty(element, clrName, jsonName, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
    }

    private static string FormatPlanMarkdown(string planJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(planJson);
            var root = doc.RootElement;
            if (!TryGetProperty(root, "MainSteps", "main_steps", out var steps))
            {
                return planJson;
            }

            var sb = new System.Text.StringBuilder();
            var i = 0;
            foreach (var step in steps.EnumerateArray())
            {
                i++;
                var title = TryGetProperty(step, "Title", "title", out var titleElement)
                    ? titleElement.GetString()
                    : $"步骤 {i}";
                sb.AppendLine($"**{i}. {title}**");

                if (TryGetProperty(step, "SubSteps", "sub_steps", out var subSteps))
                {
                    foreach (var subStep in subSteps.EnumerateArray())
                    {
                        var subTitle = TryGetProperty(subStep, "Title", "title", out var subTitleElement)
                            ? subTitleElement.GetString()
                            : "子步骤";
                        sb.AppendLine($"- {subTitle}");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch
        {
            return planJson;
        }
    }

    private static bool TryGetProperty(
        System.Text.Json.JsonElement element,
        string clrName,
        string jsonName,
        out System.Text.Json.JsonElement value)
    {
        return element.TryGetProperty(clrName, out value)
            || element.TryGetProperty(jsonName, out value);
    }

    /// <summary>
    /// 加载一条完整的演示对话，覆盖所有四个阶段的视觉元素。
    /// 这只是为了验证 UI 效果，阶段 2 接入 AF 框架时会全部移除。
    /// </summary>
    public void LoadDemoConversation()
    {
        WelcomePanel.IsVisible = false;
        MessageList.Items.Clear();

        // ════ 阶段 1：需求确认 ════

        AppendUser("帮我做一个销售数据分析功能");

        var ai1 = new AiContent
        {
            Markdown = """
                在开始之前，我需要先和你确认几个关键点：

                1. 数据源是 ERP 还是 CRM？
                2. 分析时间范围（日 / 周 / 月 / 季）？
                3. 给谁看（老板 / 销售主管 / 销售员）？
                4. 期望什么形式（图表 / 报表 / 邮件）？
                """
        };
        ai1.AddPrependedCard(MakeThinkCard(
            "思考",
            "用户没说数据源、时间范围、分析维度。先把关键点列清楚，避免后续返工。",
            statusText: null));
        ai1.AddPrependedCard(MakeToolCard(
            "调用工具 search_company_kb",
            "search_company_kb(\"销售分析模板\")\n→ 12 hits\n→ 命中样本：月度销售月报模板、区域销售对比、TOP 客户分析",
            statusText: "完成"));
        MessageList.Items.Add(ai1);

        AppendUser("ERP，按月，给老板看，PDF 邮件");

        var ai2 = new AiContent
        {
            Markdown = """
                我整理一下需求确认：

                - **数据源**：ERP
                - **粒度**：按月
                - **受众**：老板（高层视角）
                - **输出**：PDF 月报 + 邮件发送

                这样对吗？
                """
        };
        ai2.AddPrependedCard(MakeThinkCard(
            "思考 - 整理需求摘要",
            "用户已明确四个关键点。整理摘要让用户最终确认，然后进入计划阶段。",
            null));
        MessageList.Items.Add(ai2);

        AppendUser("对，开始吧");

        // ════ 阶段 2：计划编写 ════

        var ai3 = new AiContent
        {
            Markdown = """
                计划草案：

                1. **拉取 ERP 销售数据**
                   - 1.1 连接 ERP
                   - 1.2 拉取 4 月份记录
                   - 1.3 校验数据完整性
                2. **数据清洗与聚合**
                   - 2.1 缺失值处理
                   - 2.2 按月聚合
                   - 2.3 按区域 / 客户聚合
                   - 2.4 异常值标记
                3. **生成月度分析图表**
                   - 3.1 销售额趋势图
                   - 3.2 区域对比图
                   - 3.3 TOP 客户排行
                4. **撰写报告 + 邮件发送**
                   - 4.1 渲染 PDF
                   - 4.2 发送邮件

                这样安排可以吗？需要调整告诉我。
                """
        };
        ai3.AddPrependedCard(MakeThinkCard(
            "思考",
            "把任务拆成 4 个主步骤，每个主步骤进一步拆为 2-4 个子步骤。每个子步骤应能独立验收。",
            null));
        ai3.AddPrependedCard(MakeToolCard(
            "调用工具 list_existing_dashboards",
            "list_existing_dashboards()\n→ 3 found\n→ 销售月报-V2、区域季报、TOP 客户榜（参考已有的视觉风格）",
            "完成"));
        MessageList.Items.Add(ai3);

        AppendUser("把区域对比改成省份对比");

        var ai4 = new AiContent
        {
            Markdown = """
                已调整。完整计划：

                1. **拉取 ERP 销售数据**
                   - 1.1 连接 ERP
                   - 1.2 拉取 4 月份记录
                   - 1.3 校验数据完整性
                2. **数据清洗与聚合**
                   - 2.1 缺失值处理
                   - 2.2 按月聚合
                   - 2.3 按省份 / 客户聚合
                   - 2.4 异常值标记
                3. **生成月度分析图表**
                   - 3.1 销售额趋势图
                   - 3.2 **省份对比图**
                   - 3.3 TOP 客户排行
                4. **撰写报告 + 邮件发送**

                这样可以了吗？
                """
        };
        ai4.AddPrependedCard(MakeThinkCard(
            "思考 - 计划修订",
            "用户要求把区域对比改为省份对比。同步调整 2.3 聚合维度和 3.2 图表标题。",
            null));
        MessageList.Items.Add(ai4);

        AppendUser("可以，开始执行");

        // ════ 阶段 3：任务执行（时间线） ════

        var timeline = new TimelineBlock();
        MessageList.Items.Add(timeline);

        // 步骤 1（已完成，折叠）
        var s1 = timeline.AddMainStep("拉取 ERP 销售数据", "已完成", expanded: false);
        s1.AppendSubStep(MakeSubStepCompleted("连接 ERP",
            ("调用工具 erp_connect", "erp_connect(host=\"erp.local\")\n→ ok"),
            ("验收", "VERDICT: PASS")));
        s1.AppendSubStep(MakeSubStepCompleted("拉取 4 月份记录",
            ("调用工具 erp_query", "erp_query(year=2025, month=4)\n→ 12,453 rows"),
            ("验收", "VERDICT: PASS")));
        s1.AppendSubStep(MakeSubStepCompleted("校验数据完整性",
            ("调用工具 validate_records", "validate_records(rows=12453)\n→ 12,442 valid / 11 dropped"),
            ("验收", "VERDICT: PASS（11 条丢弃在容差范围内）")));

        // 步骤 2（已完成，折叠）
        var s2 = timeline.AddMainStep("数据清洗与聚合", "已完成", expanded: false);
        s2.AppendSubStep(MakeSubStepCompleted("缺失值处理",
            ("调用工具 fill_missing", "fill_missing(strategy=\"median\")\n→ 89 cells filled")));
        s2.AppendSubStep(MakeSubStepCompleted("按月聚合",
            ("调用工具 aggregate", "aggregate(by=\"month\")\n→ 1 row")));
        s2.AppendSubStep(MakeSubStepCompleted("按省份 / 客户聚合",
            ("调用工具 aggregate", "aggregate(by=[\"province\",\"customer\"])\n→ 31 + 218 rows")));
        s2.AppendSubStep(MakeSubStepCompleted("异常值标记",
            ("调用工具 flag_outliers", "flag_outliers(zscore=2.5)\n→ 4 rows flagged")));

        // 步骤 3（进行中，展开）
        var s3 = timeline.AddMainStep("生成月度分析图表", "运行中", expanded: true);

        // 子步骤 3.1（完成，折叠）
        s3.AppendSubStep(MakeSubStepCompleted("销售额趋势图",
            ("思考", "选择折线图,X 轴时间序列,Y 轴销售额。"),
            ("调用工具 plot_line_chart", "plot_line_chart(data=monthly, ...)\n→ trend.png 生成"),
            ("验收", "VERDICT: PASS")));

        // 子步骤 3.2（运行中，展开，演示验收失败重试）
        var sub32 = new SubStepCard
        {
            CardTitle = "省份对比图",
            StatusText = "验收失败，重试中",
            IsExpanded = true,
        };
        sub32.AppendDetail(MakeThinkCard("思考",
            "31 个省份用条形图最直观。降序排列让 TOP 省份突出。",
            null));
        sub32.AppendDetail(MakeToolCard("调用工具 query_db",
            "query_db(\"SELECT province, SUM(amount) FROM sales GROUP BY province\")\n→ 31 rows",
            "完成"));
        sub32.AppendDetail(MakeToolCard("调用工具 plot_bar_chart",
            "plot_bar_chart(data=..., title=\"4月省份销售对比\")\n→ province.png 生成",
            "完成"));
        sub32.AppendDetail(MakeAcceptanceCard("验收",
            "VERDICT: FAIL\n问题：图表缺少图例（legend），无法区分销售额单位。",
            "未通过"));
        sub32.AppendDetail(MakeToolCard("调用工具 plot_bar_chart",
            "plot_bar_chart(data=..., title=\"4月省份销售对比\", legend=true, unit=\"万元\")\n→ province_v2.png 生成",
            "运行中"));
        s3.AppendSubStep(sub32);

        // 子步骤 3.3（待执行，折叠）
        var sub33 = new SubStepCard
        {
            CardTitle = "TOP 客户排行",
            StatusText = "等待中",
            IsExpanded = false,
        };
        s3.AppendSubStep(sub33);

        // 步骤 4（待执行，折叠）
        var s4 = timeline.AddMainStep("撰写报告 + 邮件发送", "等待中", expanded: false);
        // 演示退步追加：上一步逻辑错误 -> 当前步追加新卡片(注释说明)
        // 此处不追加,仅留空体现"等待中"状态

        // ════ 演示授权浮窗（自动弹出） ════

        Dispatcher.UIThread.Post(() =>
        {
            ApprovalPopup.Show(
                toolName: "send_email",
                paramText: "to:         boss@company.com\nsubject:    4月销售月报\nattachment: report.pdf",
                risk: "发送邮件不可逆操作");
        }, DispatcherPriority.Loaded);

        // ════ 阶段 4：完成总结（先放进来一起预览效果） ════

        var summary = new SummaryCard
        {
            SummaryMarkdown = """
                本次任务完成了销售数据月报的生成与发送：

                - 拉取了 2025-04 全月 ERP 数据（12,453 条）
                - 生成 3 张图表 + 1 份 TOP 客户排行
                - PDF 月报已发送至老板邮箱
                - 期间因图例问题自动重试 1 次（已修复）
                """
        };
        summary.AppendStepArchive(MakeArchiveCard("拉取 ERP 销售数据", "3 子步骤 / 4 分 12 秒"));
        summary.AppendStepArchive(MakeArchiveCard("数据清洗与聚合", "4 子步骤 / 6 分 33 秒"));
        summary.AppendStepArchive(MakeArchiveCard("生成月度分析图表", "3 子步骤 / 11 分 02 秒（含 1 次重试）"));
        summary.AppendStepArchive(MakeArchiveCard("撰写报告 + 邮件发送", "2 子步骤 / 5 分 18 秒"));
        MessageList.Items.Add(summary);

        // 收尾消息（自然回到对话模式）
        var aiClose = new AiContent
        {
            Markdown = "任务已完成。如果需要调整、做新版本或新任务，告诉我就可以。"
        };
        MessageList.Items.Add(aiClose);

        // 演示连贯：紧接下一轮
        AppendUser("再做一份同样的报告但用 5 月数据");

        var aiNext = new AiContent
        {
            Markdown = "好的，我用同样的计划结构，只把数据范围换到 5 月。需要调整哪里告诉我，否则我直接开始。"
        };
        aiNext.AddPrependedCard(MakeThinkCard(
            "思考",
            "用户提出新任务，但与上一任务结构相同。可直接复用计划，只改数据范围。",
            null));
        MessageList.Items.Add(aiNext);

        ScrollToBottom();
    }

    // ──── 工厂方法 ────

    private void AppendUser(string text)
    {
        var bubble = new UserBubble { Text = text };
        MessageList.Items.Add(bubble);
    }

    private static FoldableCard MakeThinkCard(string title, string content, string? statusText)
    {
        return new FoldableCard
        {
            CardTitle = title,
            StatusText = statusText,
            IsExpanded = false,
            CardContent = MakeContentBlock(content),
        };
    }

    private static FoldableCard MakeToolCard(string title, string content, string? statusText)
    {
        return new FoldableCard
        {
            CardTitle = title,
            StatusText = statusText,
            IsExpanded = false,
            CardContent = MakeContentBlock(content, monospace: true),
        };
    }

    private static FoldableCard MakeAcceptanceCard(string title, string content, string? statusText)
    {
        return new FoldableCard
        {
            CardTitle = title,
            StatusText = statusText,
            IsExpanded = false,
            CardContent = MakeContentBlock(content),
        };
    }

    private static FoldableCard MakeArchiveCard(string title, string? statusText)
    {
        return new FoldableCard
        {
            CardTitle = title,
            StatusText = statusText,
            IsExpanded = false,
            IndentLevel = 1,
            CardContent = MakeContentBlock("（历史过程，可展开查看完整执行流）"),
        };
    }

    /// <summary>
    /// 子步骤卡片（已完成态，折叠）+ 嵌入若干明细。
    /// 明细元组：(标题, 内容)。
    /// </summary>
    private static SubStepCard MakeSubStepCompleted(string subTitle, params (string Title, string Content)[] details)
    {
        var sub = new SubStepCard
        {
            CardTitle = subTitle,
            StatusText = "已完成",
            IsExpanded = false,
        };
        foreach (var (title, content) in details)
        {
            FoldableCard card;
            if (title.StartsWith("调用工具", StringComparison.Ordinal))
                card = MakeToolCard(title, content, "完成");
            else if (title.StartsWith("验收", StringComparison.Ordinal))
                card = MakeAcceptanceCard(title, content, "通过");
            else
                card = MakeThinkCard(title, content, null);
            card.IndentLevel = 1; // 不再缩进 50px
            sub.AppendDetail(card);
        }
        return sub;
    }

    private static Control MakeContentBlock(string text, bool monospace = false)
    {
        var tb = new SelectableTextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Avalonia.Media.Brushes.LightGray,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Padding = new Thickness(0, 2, 0, 2),
        };
        if (monospace)
            tb.FontFamily = new Avalonia.Media.FontFamily("Consolas, 'Cascadia Code', monospace");
        return tb;
    }

    // ──── 消息区滚动 ────

    private void OnScrollToBottomClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ForceScrollToBottom();
    }

    internal void ScrollToBottom()
    {
        if (_userScrolledUp)
        {
            return;
        }

        ForceScrollToBottom();
    }

    internal void ForceScrollToBottom()
    {
        _userScrolledUp = false;
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var isAtBottom = MessageScroller.Offset.Y + MessageScroller.Viewport.Height >= MessageScroller.Extent.Height - 30;
        _userScrolledUp = !isAtBottom;
        ScrollToBottomBtn.IsVisible = _userScrolledUp;
    }
}
