using System.Collections.Concurrent;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Reliability;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式执行器，负责启动和管理工作流的执行。
/// 维护每个任务的对话历史，支持多轮对话。
/// </summary>
public sealed class WorkflowExecutor
{
    private readonly GeneralManagerAgentBuilder _agentBuilder;
    private readonly WorkTaskService _taskService;
    private readonly WorkExecutionLogService _logService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly AgentService _agentService;
    private readonly IPublisher _publisher;
    private readonly ILogger<WorkflowExecutor> _logger;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly WorkTaskCancellationRegistry _cancellationRegistry;
    private readonly WorkTaskTitleService _titleService;
    private readonly WorkTaskContextService _contextService;
    private readonly WorkTaskFileService _fileService;
    private readonly ProjectLeadService _projectLeadService;
    private readonly ILogger<ProjectLeadTools> _projectLeadToolsLogger;
    private readonly RunningStepInterruptService _runningStepInterruptService;

    private readonly ConcurrentDictionary<string, List<ChatMessage>> _chatHistoriesByRun = new();

    public WorkflowExecutor(
        GeneralManagerAgentBuilder agentBuilder,
        WorkTaskService taskService,
        WorkExecutionLogService logService,
        AiProviderService providerService,
        AiModelService modelService,
        AgentService agentService,
        IPublisher publisher,
        ILogger<WorkflowExecutor> logger,
        DeadlockDetector deadlockDetector,
        WorkTaskCancellationRegistry cancellationRegistry,
        WorkTaskTitleService titleService,
        WorkTaskContextService contextService,
        WorkTaskFileService fileService,
        ProjectLeadService projectLeadService,
        ILogger<ProjectLeadTools> projectLeadToolsLogger,
        RunningStepInterruptService runningStepInterruptService)
    {
        _agentBuilder = agentBuilder ?? throw new ArgumentNullException(nameof(agentBuilder));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deadlockDetector = deadlockDetector ?? throw new ArgumentNullException(nameof(deadlockDetector));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _titleService = titleService ?? throw new ArgumentNullException(nameof(titleService));
        _contextService = contextService ?? throw new ArgumentNullException(nameof(contextService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _projectLeadService = projectLeadService ?? throw new ArgumentNullException(nameof(projectLeadService));
        _projectLeadToolsLogger = projectLeadToolsLogger ?? throw new ArgumentNullException(nameof(projectLeadToolsLogger));
        _runningStepInterruptService = runningStepInterruptService ?? throw new ArgumentNullException(nameof(runningStepInterruptService));
    }

    public async Task ExecuteAsync(
        string taskId,
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        string userInput,
        List<AgentMention>? mentions = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteCoreAsync(taskId, agent, provider, model, userInput, mentions, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteCoreAsync(
        string taskId,
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        string userInput,
        List<AgentMention>? mentions,
        string? systemMessage,
        CancellationToken cancellationToken)
    {
        using var nonExpertScope = NonExpertProcessScope.Enter();
        _logger.LogInformation("工作任务 {TaskId} 新轮次，输入：{Input}", taskId, userInput);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_cancellationRegistry.TryRegister(taskId, linkedCts))
        {
            throw new InvalidOperationException("当前工作任务已有执行轮次在运行，请等待当前轮次结束或先取消任务。");
        }

        try
        {
            var aiAgent = mentions is { Count: > 0 }
                ? await _agentBuilder.BuildWithSubAgentsAsync(
                    agent,
                    provider,
                    model,
                    taskId,
                    mentions,
                    _providerService,
                    _modelService,
                    linkedCts.Token)
                : await _agentBuilder.BuildAsync(agent, provider, model, taskId, linkedCts.Token);

            var task = _taskService.GetById(taskId)
                ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
            var isFirstRun = string.IsNullOrEmpty(task.RunId);
            var runId = task.RunId;

            if (isFirstRun)
            {
                runId = Guid.NewGuid().ToString("N");
                _taskService.SetRunId(taskId, runId);
                _taskService.SetManagerRunId(taskId, runId);

                await _publisher.PublishAsync(
                    Events.OnWorkTaskCreated,
                    new WorkTaskCreatedArgs(taskId, task.SessionId, task.Title));

                _ = Task.Run(
                    () => _titleService.GenerateInitialTitleAsync(taskId, CancellationToken.None),
                    CancellationToken.None);
            }

            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new InvalidOperationException($"工作任务缺少 RunId：{taskId}");
            }

            // A 层上下文按 RunId 隔离，重启后可从 WorkTaskContextMessages 恢复。
            var history = _chatHistoriesByRun.GetOrAdd(runId, _ => LoadRunHistory(taskId, runId));

            if (!string.IsNullOrWhiteSpace(systemMessage) && history.Count == 0)
            {
                var message = new ChatMessage(ChatRole.System, systemMessage);
                history.Add(message);
                _contextService.AppendMessage(taskId, runId, ChatRole.System.ToString(), systemMessage);
            }

            // 追加用户消息；本轮请求额外注入工作快照，避免 A 层在 B/C 执行后丢失上下文。
            var userMessage = new ChatMessage(ChatRole.User, userInput);
            var requestHistory = new List<ChatMessage>(history);
            var workSnapshot = BuildWorkSnapshot(taskId);
            if (!string.IsNullOrWhiteSpace(workSnapshot))
            {
                requestHistory.Add(new ChatMessage(ChatRole.System, workSnapshot));
            }

            requestHistory.Add(userMessage);
            history.Add(userMessage);
            _contextService.AppendMessage(taskId, runId, ChatRole.User.ToString(), userInput);

            // 流式执行，实时发布思考/工具调用/文本事件
            var streamProcessor = new WorkModeStreamProcessor(taskId, _publisher, _logService, _deadlockDetector);

            await foreach (var chunk in aiAgent.RunStreamingAsync(requestHistory, cancellationToken: linkedCts.Token))
            {
                if (chunk.Contents.Count > 0)
                {
                    await streamProcessor.ProcessChunkAsync(chunk.Contents, linkedCts.Token);
                }
            }

            // 刷出剩余文本
            await streamProcessor.FlushAsync(linkedCts.Token);

            // 将完整的 assistant 回复追加到历史（用于下一轮上下文）
            var assistantText = streamProcessor.GetAccumulatedText();
            if (!string.IsNullOrEmpty(assistantText))
            {
                history.Add(new ChatMessage(ChatRole.Assistant, assistantText));
                _contextService.AppendMessage(taskId, runId, ChatRole.Assistant.ToString(), assistantText);
            }

            _logger.LogInformation("工作任务 {TaskId} 轮次完成", taskId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("工作任务 {TaskId} 被取消", taskId);
            var task = _taskService.GetById(taskId);
            if (task is { IsActive: true })
            {
                _taskService.RecordExecutionCancelled(taskId);
                await _publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(taskId));
            }
            RemoveRunHistory(taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作任务 {TaskId} 执行失败", taskId);
            _taskService.RecordExecutionFailed(taskId, ex.Message);
            await _publisher.PublishAsync(Events.OnWorkTaskFailed, new WorkTaskFailedArgs(taskId, ex.Message));
        }
        finally
        {
            _cancellationRegistry.TryUnregister(taskId);
        }
    }

    public Task ContinueAsync(
        string taskId,
        string userInput,
        List<AgentMention>? mentions = null,
        CancellationToken cancellationToken = default)
    {
        var task = _taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
        var provider = _providerService.GetById(task.Provider)
            ?? throw new InvalidOperationException($"工作任务缺少有效 AI 提供商：{task.Provider}");
        var model = _modelService.GetById(task.Model)
            ?? throw new InvalidOperationException($"工作任务缺少有效模型：{task.Model}");
        var agent = _agentService.GetByName(task.AgentName)
            ?? throw new InvalidOperationException($"工作任务缺少有效智能体：{task.AgentName}");

        // 当前阶段的 Continue 是基于同一 taskId 和内存 ChatHistory 的轻量续跑。
        // AF/HITL ResumeAsync 会在阶段 3 接管 PendingRequest 场景。
        return ExecuteAsync(taskId, agent, provider, model, userInput, mentions, cancellationToken);
    }

    public async Task ExecuteFromHandoffAsync(
        string taskId,
        string userInput,
        string? planJson,
        List<AgentMention>? mentions,
        string systemMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        if (!string.IsNullOrWhiteSpace(planJson))
        {
            _taskService.UpdatePlan(taskId, planJson);
            _taskService.ResetOrchestratorForNewPlan(taskId);
            await _publisher.PublishAsync(
                Events.OnWorkPlanUpdated,
                new WorkPlanUpdatedArgs(taskId, planJson)).ConfigureAwait(false);
        }

        var task = _taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
        var provider = _providerService.GetById(task.Provider)
            ?? throw new InvalidOperationException($"工作任务缺少有效 AI 提供商：{task.Provider}");
        var model = _modelService.GetById(task.Model)
            ?? throw new InvalidOperationException($"工作任务缺少有效模型：{task.Model}");
        var agent = _agentService.GetByName(task.AgentName)
            ?? throw new InvalidOperationException($"工作任务缺少有效智能体：{task.AgentName}");

        await ExecuteCoreAsync(
            taskId,
            agent,
            provider,
            model,
            userInput,
            mentions,
            systemMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(string taskId, string userResponse, CancellationToken cancellationToken = default)
    {
        var task = _taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
        if (string.IsNullOrWhiteSpace(task.PendingRequestId))
        {
            _logger.LogInformation("工作任务 {TaskId} 没有待处理请求，忽略重复恢复输入。", taskId);
            return;
        }

        var rows = _taskService.TryClearPendingRequest(taskId, task.PendingRequestId);
        if (rows == 0)
        {
            _logger.LogInformation("工作任务 {TaskId} 的待处理请求已被其他轮次消费，忽略重复恢复输入。", taskId);
            return;
        }

        if (string.Equals(task.PendingRequestKind, PlanTools.PlanConfirmationKind, StringComparison.Ordinal)
            && IsAffirmativePlanConfirmation(userResponse))
        {
            await FinalizeConfirmedPlanAsync(taskId, userResponse, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ContinueAsync(taskId, userResponse, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeConfirmedPlanAsync(string taskId, string userResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AppendRunHistory(taskId, ChatRole.User, userResponse);

        await _publisher.PublishAsync(
            Events.OnWorkAssistantDelta,
            new WorkAssistantDeltaArgs(taskId, null, "已确认，正在交付项目组长执行。\n")).ConfigureAwait(false);

        var tools = new ProjectLeadTools(
            _fileService,
            _projectLeadService,
            _projectLeadToolsLogger,
            taskId,
            _taskService,
            _cancellationRegistry);

        var result = await tools.CreateFinalizePlanTool()
            .InvokeAsync(new AIFunctionArguments(), cancellationToken)
            .ConfigureAwait(false);
        var resultText = result?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(resultText))
        {
            AppendRunHistory(taskId, ChatRole.Assistant, resultText);
            await _publisher.PublishAsync(
                Events.OnWorkAssistantDelta,
                new WorkAssistantDeltaArgs(taskId, null, resultText + Environment.NewLine)).ConfigureAwait(false);
        }

        var task = _taskService.GetById(taskId);
        if (string.Equals(task?.OrchestratorState, WorkTaskOrchestratorStates.Running, StringComparison.Ordinal))
        {
            _logger.LogInformation("工作任务 {TaskId} 已通过计划确认直接启动项目组长。", taskId);
            return;
        }

        _logger.LogWarning("工作任务 {TaskId} 计划确认后未能启动项目组长：{Result}", taskId, resultText);
    }

    private static bool IsAffirmativePlanConfirmation(string input)
    {
        var normalized = (input ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var compact = normalized
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return compact is "执行" or "执行吧" or "开始" or "开始执行" or "确认" or "确认执行" or "同意" or "可以" or "好" or "好的" or "ok" or "yes" or "go"
            || compact.Contains("执行", StringComparison.Ordinal)
            || compact.Contains("开始", StringComparison.Ordinal)
            || compact.Contains("确认", StringComparison.Ordinal);
    }

    private void AppendRunHistory(string taskId, ChatRole role, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var task = _taskService.GetById(taskId);
        if (string.IsNullOrWhiteSpace(task?.RunId))
        {
            return;
        }

        var history = _chatHistoriesByRun.GetOrAdd(task.RunId, _ => LoadRunHistory(taskId, task.RunId));
        history.Add(new ChatMessage(role, content));
        _contextService.AppendMessage(taskId, task.RunId, role.ToString(), content);
    }

    public async Task CancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await _cancellationRegistry.CancelAsync(taskId);
        MarkPlanCancelled(taskId);

        var task = _taskService.GetById(taskId);
        var shouldPublish = task is { IsActive: true };
        if (shouldPublish)
        {
            _taskService.RecordExecutionCancelled(taskId);
        }

        RemoveRunHistory(taskId);
        if (shouldPublish)
        {
            await _publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(taskId));
        }
    }

    public async Task PauseAsync(string taskId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        RequestPlanPause(taskId);
        _taskService.SetPreemption(taskId, true);
        _taskService.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.PauseRequested, touchHeartbeat: false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<bool> InterruptCurrentStepAsync(string taskId, string interruptInput, CancellationToken cancellationToken = default)
        => await _runningStepInterruptService.InterruptCurrentStepAsync(taskId, interruptInput, cancellationToken).ConfigureAwait(false);

    private void MarkPlanCancelled(string taskId)
    {
        var plan = _fileService.LoadPlan(taskId);
        if (plan is null)
        {
            return;
        }

        plan.Status = WorkTaskPlanStatuses.Cancelled;
        _fileService.SavePlan(plan);
    }

    private void RequestPlanPause(string taskId)
    {
        var plan = _fileService.LoadPlan(taskId);
        if (plan is null)
        {
            return;
        }

        if (!string.Equals(plan.Status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal)
            && !string.Equals(plan.Status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal)
            && !string.Equals(plan.Status, WorkTaskPlanStatuses.Failed, StringComparison.Ordinal))
        {
            plan.Status = WorkTaskPlanStatuses.Running;
        }

        _fileService.SavePlan(plan);
    }

    private List<ChatMessage> LoadRunHistory(string taskId, string runId)
    {
        var messages = _contextService.ListMessages(taskId, runId);
        return messages
            .Select(static message => new ChatMessage(new ChatRole(message.Role), message.Content))
            .ToList();
    }

    private string? BuildWorkSnapshot(string taskId)
    {
        var task = _taskService.GetById(taskId);
        if (task is null)
        {
            return null;
        }

        var plan = _fileService.LoadPlan(taskId);
        var logs = _logService.ListByTask(taskId);
        if (plan is null && logs.Count == 0 && string.IsNullOrWhiteSpace(task.FinalReport))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 当前工作任务快照");
        sb.AppendLine("这段上下文来自工作模式执行状态，供总经理在后续对话中讨论已有成果、追加步骤或调整计划。不要把它当成用户的新要求。");
        sb.AppendLine();
        sb.AppendLine($"- 任务标题：{task.Title}");
        sb.AppendLine($"- 初始目标：{task.InitialInput}");
        sb.AppendLine($"- 任务状态：{(task.IsActive ? "活跃" : "已结束")}");
        sb.AppendLine($"- B 层状态：{task.OrchestratorState ?? "未启动"}");
        if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
        {
            sb.AppendLine($"- 结束原因：{task.ErrorMessage}");
        }

        if (plan is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## 当前计划");
            sb.AppendLine($"- plan 状态：{plan.Status}");
            sb.AppendLine($"- 步骤总数：{plan.Steps.Count}");
            foreach (var step in plan.Steps.Take(80))
            {
                var summary = !string.IsNullOrWhiteSpace(step.Summary)
                    ? step.Summary
                    : !string.IsNullOrWhiteSpace(step.LastError)
                        ? $"错误：{step.LastError}"
                        : "暂无摘要";
                sb.AppendLine($"- [{step.Status}] {step.Id} {step.Title}：{summary}");
            }

            if (plan.Steps.Count > 80)
            {
                sb.AppendLine($"- ……其余 {plan.Steps.Count - 80} 个步骤省略。");
            }
        }

        if (!string.IsNullOrWhiteSpace(task.FinalReport))
        {
            sb.AppendLine();
            sb.AppendLine("## 最终报告/当前总结");
            sb.AppendLine(TrimForSnapshot(task.FinalReport, 4000));
        }

        var recentLogs = logs
            .Where(static log => log.LogType is WorkExecutionLogTypes.StepStart
                or WorkExecutionLogTypes.StepComplete
                or WorkExecutionLogTypes.Acceptance
                or WorkExecutionLogTypes.DeadlockDetected)
            .TakeLast(30)
            .ToList();
        if (recentLogs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 最近执行记录");
            foreach (var log in recentLogs)
            {
                sb.AppendLine($"- #{log.Sequence} {log.LogType}: {TrimForSnapshot(log.Content, 600)}");
            }
        }

        return sb.ToString();
    }

    private static string TrimForSnapshot(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "……";
    }

    private void RemoveRunHistory(string taskId)
    {
        var task = _taskService.GetById(taskId);
        if (!string.IsNullOrWhiteSpace(task?.RunId))
        {
            _chatHistoriesByRun.TryRemove(task.RunId, out _);
        }
    }
}
