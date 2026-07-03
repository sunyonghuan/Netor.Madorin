using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.ProjectLead;

/// <summary>
/// B 层项目组长服务。它是确定性的 C# 主循环，不进入 AF Workflow。
/// </summary>
public sealed class ProjectLeadService(
    WorkTaskFileService fileService,
    IProjectStepDispatcher dispatcher,
    IPublisher publisher,
    ILogger<ProjectLeadService> logger,
    WorkTaskService? taskService = null,
    WorkTaskEventService? eventService = null,
    WorkExecutionLogService? logService = null,
    AgentService? agentService = null)
{
    public async Task RunAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var nonExpertScope = NonExpertProcessScope.Enter();

        try
        {
            var plan = fileService.LoadPlan(taskId)
                ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");

            if (IsStopped(plan.Status))
            {
                logger.LogInformation("任务 {TaskId} 处于 {Status} 状态，项目组长不推进。", taskId, plan.Status);
                return;
            }

            plan.Status = WorkTaskPlanStatuses.Running;
            fileService.SavePlan(plan);
            SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
            Touch(taskId);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plan = fileService.LoadPlan(taskId)
                    ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
                Touch(taskId);

                if (IsStopped(plan.Status))
                {
                    logger.LogInformation("任务 {TaskId} 推进中检测到 {Status}。", taskId, plan.Status);
                    if (string.Equals(plan.Status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal))
                    {
                        var published = MarkCancelled(taskId);
                        if (published)
                        {
                            await publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(taskId)).ConfigureAwait(false);
                        }
                    }
                    return;
                }

                if (HasPreemption(taskId))
                {
                    PauseForUserPreemption(taskId, plan);
                    await publisher.PublishAsync(
                        Events.OnWorkTaskPaused,
                        new WorkTaskPausedArgs(taskId, "user_preemption")).ConfigureAwait(false);
                    return;
                }

                if (CollapseCompletedExpandableSteps(plan))
                {
                    fileService.SavePlan(plan);
                    Touch(taskId);
                    continue;
                }

                var step = FindNextExecutableStep(plan);
                if (step is null)
                {
                    if (plan.Steps.Any(static item => string.Equals(item.Status, WorkTaskPlanStepStatuses.Failed, StringComparison.Ordinal)))
                    {
                        plan.Status = WorkTaskPlanStatuses.Failed;
                        fileService.SavePlan(plan);
                        taskService?.RecordExecutionFailed(taskId, "计划存在失败步骤，项目组长已停止推进。");
                        Touch(taskId);
                        await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Error, "计划存在失败步骤，项目组长已停止推进。").ConfigureAwait(false);
                        return;
                    }

                    plan.Status = WorkTaskPlanStatuses.Done;
                    fileService.SavePlan(plan);
                    var finalReport = BuildSummary(plan);
                    taskService?.RecordExecutionCompleted(taskId, finalReport);
                    Touch(taskId);
                    await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
                    await publisher.PublishAsync(
                        Events.OnWorkTaskCompleted,
                        CreateCompletedArgs(taskId, finalReport)).ConfigureAwait(false);
                    return;
                }

                if (step.IsExpandable && step.ExpandedAt is null)
                {
                    await ExpandStepAsync(taskId, step, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RunStepAsync(taskId, step, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var published = MarkCancelled(taskId);
            if (published)
            {
                await publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(taskId)).ConfigureAwait(false);
            }
        }
    }

    private async Task ExpandStepAsync(
        string taskId,
        WorkTaskPlanStepFile step,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.ExpanderRole))
        {
            MarkStepFailed(taskId, step.Id, $"可展开步骤 {step.Id} 缺少 expander_role。");
            return;
        }

        var plan = fileService.LoadPlan(taskId)
            ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
        var current = plan.Steps.First(item => string.Equals(item.Id, step.Id, StringComparison.Ordinal));
        current.Status = WorkTaskPlanStepStatuses.Running;
        current.Attempts++;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        plan.Status = WorkTaskPlanStatuses.Running;
        fileService.SavePlan(plan);
        Touch(taskId);

        await publisher.PublishAsync(
            Events.OnWorkStepStarted,
            new WorkStepStartedArgs(taskId, $"展开计划：{step.Title}", step.ExpanderRole)).ConfigureAwait(false);

        try
        {
            var environment = fileService.LoadEnvironment(taskId);
            var expansionStep = CreateExpansionDispatchStep(current);
            var expansionJson = await dispatcher.DispatchAsync(taskId, expansionStep, environment, cancellationToken).ConfigureAwait(false);
            var childSteps = WorkTaskPlanStepJsonParser.ParseSteps(expansionJson, current.Id, current.Depth + 1);

            plan = fileService.LoadPlan(taskId)
                ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
            InsertExpandedSteps(plan, current.Id, childSteps);
            fileService.SavePlan(plan);
            Touch(taskId);

            await publisher.PublishAsync(
                Events.OnWorkStepCompleted,
                new WorkStepCompletedArgs(taskId, $"展开计划：{step.Title}", $"已展开为 {childSteps.Count} 个子步骤。")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "任务 {TaskId} 步骤 {StepId} 展开失败。", taskId, step.Id);
            MarkStepFailed(taskId, step.Id, ex.Message);
            taskService?.RecordExecutionFailed(taskId, ex.Message);
            await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Error, $"步骤 {step.Title} 展开失败：{ex.Message}").ConfigureAwait(false);
            await publisher.PublishAsync(
                Events.OnWorkTaskFailed,
                new WorkTaskFailedArgs(taskId, ex.Message)).ConfigureAwait(false);
        }
    }

    private async Task RunStepAsync(
        string taskId,
        WorkTaskPlanStepFile step,
        CancellationToken cancellationToken)
    {
        step.Status = WorkTaskPlanStepStatuses.Running;
        step.Attempts++;
        step.UpdatedAt = DateTimeOffset.UtcNow;
        var plan = fileService.LoadPlan(taskId)
            ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
        var current = plan.Steps.First(item => string.Equals(item.Id, step.Id, StringComparison.Ordinal));
        current.Status = step.Status;
        current.Attempts = step.Attempts;
        current.UpdatedAt = step.UpdatedAt;
        plan.Status = WorkTaskPlanStatuses.Running;
        fileService.SavePlan(plan);
        Touch(taskId);

        var department = ResolveMainStepTitle(step);
        var context = BuildStepContext(step);
        var startRecord = new WorkExecutionLogRecords.StepStart(step.Title, department, context);
        logService?.Append(
            taskId,
            WorkExecutionLogTypes.StepStart,
            JsonSerializer.Serialize(startRecord, WorkModeJsonContext.Default.StepStart));

        await publisher.PublishAsync(
            Events.OnWorkStepStarted,
            new WorkStepStartedArgs(taskId, step.Title, department)).ConfigureAwait(false);

        try
        {
            var environment = fileService.LoadEnvironment(taskId);
            var summary = await dispatcher.DispatchAsync(taskId, step, environment, cancellationToken).ConfigureAwait(false);
            plan = fileService.UpdateStepStatus(taskId, step.Id, WorkTaskPlanStepStatuses.Done, summary);
            Touch(taskId);

            await publisher.PublishAsync(
                Events.OnWorkStepCompleted,
                new WorkStepCompletedArgs(taskId, step.Title, summary)).ConfigureAwait(false);
            var completeRecord = new WorkExecutionLogRecords.StepComplete(step.Title, summary);
            logService?.Append(
                taskId,
                WorkExecutionLogTypes.StepComplete,
                JsonSerializer.Serialize(completeRecord, WorkModeJsonContext.Default.StepComplete));
            if (step.IsMilestone)
            {
                await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Milestone, summary).ConfigureAwait(false);
            }

            if (string.Equals(plan.Status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal))
            {
                var finalReport = BuildSummary(plan);
                taskService?.RecordExecutionCompleted(taskId, finalReport);
                await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
                await publisher.PublishAsync(
                    Events.OnWorkTaskCompleted,
                    CreateCompletedArgs(taskId, finalReport)).ConfigureAwait(false);
            }

            logger.LogInformation("任务 {TaskId} 步骤 {StepId} 已完成。", taskId, step.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "任务 {TaskId} 步骤 {StepId} 执行失败。", taskId, step.Id);
            var failedPlan = fileService.UpdateStepStatus(taskId, step.Id, WorkTaskPlanStepStatuses.Failed, error: ex.Message);
            failedPlan.Status = WorkTaskPlanStatuses.Failed;
            fileService.SavePlan(failedPlan);
            taskService?.RecordExecutionFailed(taskId, ex.Message);
            Touch(taskId);
            await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Error, $"步骤 {step.Title} 执行失败：{ex.Message}").ConfigureAwait(false);
            await publisher.PublishAsync(
                Events.OnWorkTaskFailed,
                new WorkTaskFailedArgs(taskId, ex.Message)).ConfigureAwait(false);
        }
    }

    private static bool IsStopped(string status)
    {
        return string.Equals(status, WorkTaskPlanStatuses.Paused, StringComparison.Ordinal)
            || string.Equals(status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal)
            || string.Equals(status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal)
            || string.Equals(status, WorkTaskPlanStatuses.Failed, StringComparison.Ordinal);
    }

    private bool HasPreemption(string taskId)
        => taskService?.GetById(taskId)?.HasPreemption == true;

    private void PauseForUserPreemption(string taskId, WorkTaskPlanFile plan)
    {
        plan.Status = WorkTaskPlanStatuses.Paused;
        fileService.SavePlan(plan);
        taskService?.SetPreemption(taskId, false);
        SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Paused, touchHeartbeat: false);
        Touch(taskId);
    }

    private static WorkTaskPlanStepFile? FindNextExecutableStep(WorkTaskPlanFile plan)
    {
        return plan.Steps.FirstOrDefault(static item =>
            string.Equals(item.Status, WorkTaskPlanStepStatuses.Pending, StringComparison.Ordinal)
            && (!item.IsExpandable || item.ExpandedAt is null));
    }

    private static bool CollapseCompletedExpandableSteps(WorkTaskPlanFile plan)
    {
        var changed = false;
        for (var i = plan.Steps.Count - 1; i >= 0; i--)
        {
            var step = plan.Steps[i];
            if (!step.IsExpandable
                || step.ExpandedAt is null
                || !string.Equals(step.Status, WorkTaskPlanStepStatuses.Pending, StringComparison.Ordinal))
            {
                continue;
            }

            var childSteps = plan.Steps
                .Where(child => string.Equals(child.ParentStepId, step.Id, StringComparison.Ordinal))
                .ToList();
            if (childSteps.Count == 0
                || !childSteps.All(static child => string.Equals(child.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal)))
            {
                continue;
            }

            step.Status = WorkTaskPlanStepStatuses.Done;
            step.Summary = $"已展开并完成 {childSteps.Count} 个子步骤。";
            step.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        return changed;
    }

    private static WorkTaskPlanStepFile CreateExpansionDispatchStep(WorkTaskPlanStepFile step)
    {
        return new WorkTaskPlanStepFile
        {
            Id = step.Id,
            Title = step.Title,
            Role = step.ExpanderRole,
            Input = $$"""
                请把当前阶段展开为可直接执行的子步骤，并只返回 JSON。

                父步骤：
                - id: {{step.Id}}
                - title: {{step.Title}}
                - role: {{step.Role}}

                父步骤输入：
                {{step.Input}}

                JSON 格式：
                {
                  "steps": [
                    {
                      "id": "{{step.Id}}-01",
                      "title": "子步骤标题",
                      "role": "负责执行的专员 role",
                      "input": "给专员的完整输入",
                      "is_milestone": false,
                      "expandable": false,
                      "expander_role": ""
                    }
                  ]
                }
                """,
            Status = WorkTaskPlanStepStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private void InsertExpandedSteps(
        WorkTaskPlanFile plan,
        string parentStepId,
        IReadOnlyCollection<WorkTaskPlanStepFile> childSteps)
    {
        var parentIndex = plan.Steps.FindIndex(item => string.Equals(item.Id, parentStepId, StringComparison.Ordinal));
        if (parentIndex < 0)
        {
            throw new InvalidOperationException($"可展开父步骤不存在：{parentStepId}");
        }

        var existingIds = plan.Steps
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var child in childSteps)
        {
            if (existingIds.Contains(child.Id))
            {
                throw new InvalidOperationException($"展开子步骤 id 与现有计划冲突：{child.Id}");
            }

            if (child.IsExpandable && string.IsNullOrWhiteSpace(child.ExpanderRole))
            {
                throw new InvalidOperationException($"展开子步骤 {child.Id} 是可展开步骤，但缺少 expander_role。");
            }
        }

        var parent = plan.Steps[parentIndex];
        parent.Status = WorkTaskPlanStepStatuses.Pending;
        parent.ExpandedAt = DateTimeOffset.UtcNow;
        parent.Summary = $"已展开为 {childSteps.Count} 个子步骤。";
        parent.UpdatedAt = DateTimeOffset.UtcNow;
        plan.Steps.InsertRange(parentIndex + 1, childSteps);
    }

    private void MarkStepFailed(string taskId, string stepId, string error)
    {
        var failedPlan = fileService.UpdateStepStatus(taskId, stepId, WorkTaskPlanStepStatuses.Failed, error: error);
        failedPlan.Status = WorkTaskPlanStatuses.Failed;
        fileService.SavePlan(failedPlan);
        taskService?.RecordExecutionFailed(taskId, error);
        Touch(taskId);
    }

    private bool MarkCancelled(string taskId)
    {
        var plan = fileService.LoadPlan(taskId);
        if (plan is not null && !string.Equals(plan.Status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal))
        {
            plan.Status = WorkTaskPlanStatuses.Cancelled;
            fileService.SavePlan(plan);
        }

        var task = taskService?.GetById(taskId);
        if (task is not { IsActive: true })
        {
            return false;
        }

        taskService!.RecordExecutionCancelled(taskId);
        Touch(taskId);
        return true;
    }

    private WorkTaskCompletedArgs CreateCompletedArgs(string taskId, string finalReport)
    {
        var task = taskService?.GetById(taskId);
        var agent = task is null ? null : agentService?.GetByName(task.AgentName);
        var completedAt = task?.CompletedAt ?? DateTimeOffset.Now.ToUnixTimeMilliseconds();

        return new WorkTaskCompletedArgs(
            TaskId: taskId,
            FinalReport: finalReport,
            ManagerAgentId: task?.AgentName ?? string.Empty,
            ManagerAgentName: agent?.Name ?? task?.AgentName ?? string.Empty,
            SourceSessionId: task?.SessionId ?? string.Empty,
            WorkspaceId: task?.WorkspaceId ?? string.Empty,
            CompletedAt: completedAt,
            AllowMemoryIngest: agent?.AllowWorkflowMemory ?? true);
    }

    private void Touch(string taskId)
    {
        taskService?.TouchOrchestrator(taskId);
    }

    private void SetOrchestratorState(string taskId, string state, bool touchHeartbeat)
    {
        taskService?.SetOrchestratorState(taskId, state, touchHeartbeat);
    }

    private async Task CreateTaskEventAsync(string taskId, string kind, string message)
    {
        if (eventService is null)
        {
            return;
        }

        var task = taskService?.GetById(taskId);
        var entity = eventService.Create(taskId, kind, message, task?.ManagerRunId);
        await publisher.PublishAsync(
            Events.OnWorkTaskEventCreated,
            new WorkTaskEventCreatedArgs(taskId, entity.Id, entity.Kind, entity.Message)).ConfigureAwait(false);
    }

    private static string ResolveMainStepTitle(WorkTaskPlanStepFile step)
    {
        const string prefix = "主步骤：";
        var line = step.Input
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .FirstOrDefault(item => item.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        var title = line is null ? null : line.Trim()[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(title) ? "未分组步骤" : title;
    }

    private static string BuildStepContext(WorkTaskPlanStepFile step)
    {
        return $"""
            步骤 ID：{step.Id}

            {step.Input}
            """;
    }

    private static string BuildSummary(WorkTaskPlanFile plan)
    {
        var lines = new List<string>
        {
            $"计划状态：{plan.Status}",
            $"完成步骤：{plan.Steps.Count(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal))}/{plan.Steps.Count}"
        };

        foreach (var step in plan.Steps)
        {
            var summary = !string.IsNullOrWhiteSpace(step.Summary)
                ? step.Summary
                : !string.IsNullOrWhiteSpace(step.LastError)
                    ? $"失败：{step.LastError}"
                    : "暂无摘要";
            lines.Add($"- [{step.Status}] {step.Title}: {summary}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
