using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Workflow.Builders;
using Netor.Cortana.AI.Workflow.Title;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Collections.Concurrent;

namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// 默认 <see cref="IWorkflowExecutor"/> 实现。
///
/// 阶段 2B 占位：仅打通数据库 + 事件路径，不接入真实 SDK Workflow。
/// 阶段 3B 升级：
/// - SubMode = "groupchat" 走 SDK 真实编排（<see cref="GroupChatWorkflowFactory"/> + <see cref="InProcessExecution"/>）
/// - SubMode = 其他保留占位实现（推到阶段 4B 接入 magentic / parallelanalysis 等）
/// - StartTaskAsync 立即返回 taskId，后台 Task.Run 启动执行循环
/// - 任务运行期通过 <see cref="_runningTasks"/> 追踪，CancelTaskAsync 真实生效
/// - StopAsync 取消所有运行中的任务，等待 5 秒后强制返回
///
/// IHostedService：
/// - StartAsync：扫描 OrchestrationTask 表中 status 为 Running/Paused/Pending 的"内存中无 Run"
///   的孤儿任务，全部标记为 Failed（决策 9-A）。
/// - StopAsync：取消所有运行中任务并等待结束。
///
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B / §3B。
/// </summary>
public sealed class WorkflowExecutor(
    WorkflowTaskRepository taskRepo,
    WorkflowStepRepository stepRepo,
    WorkflowExecutorOptions options,
    IWorkflowTitleGenerator titleGenerator,
    AIAgentFactory factory,
    AgentService agentService,
    AiProviderService providerService,
    AiModelService modelService,
    IPublisher publisher,
    ILogger<WorkflowExecutor> logger) : IWorkflowExecutor, IHostedService
{
    /// <summary>
    /// 运行中任务追踪表。键 = taskId，值 = 任务运行期上下文。
    /// 当 GroupChat 等真实编排子模式启动后台执行循环时，向此表插入条目；
    /// 任务正常结束 / 异常 / 取消时从此表移除。
    /// </summary>
    private readonly ConcurrentDictionary<string, RunningTaskContext> _runningTasks = new();

    // ──── IHostedService：启动孤儿清理（决策 9-A） + 关闭时取消运行中任务 ────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var orphans = taskRepo.QueryByStatuses([
                WorkflowTaskStatus.Running.ToDbValue(),
                WorkflowTaskStatus.Paused.ToDbValue(),
                WorkflowTaskStatus.Pending.ToDbValue(),
            ]);

            if (orphans.Count == 0)
            {
                logger.LogDebug("WorkflowExecutor 启动孤儿扫描：无活跃任务遗留。");
                return Task.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var orphan in orphans)
            {
                taskRepo.UpdateCompleted(
                    orphan.Id,
                    status: WorkflowTaskStatus.Failed.ToDbValue(),
                    finalReport: null,
                    errorMessage: "宿主重启导致任务丢失，请重新启动该任务。",
                    completedAt: now,
                    lastActiveTimestamp: now);

                publisher.Publish(Events.OnWorkflowTaskFailed, new WorkflowTaskFailedArgs(
                    TaskId: orphan.Id,
                    SourceSessionId: orphan.SourceSessionId,
                    TraceId: orphan.TraceId,
                    WorkspaceId: orphan.WorkspaceId,
                    Mode: orphan.Mode,
                    SubMode: orphan.SubMode,
                    OccurredAt: DateTimeOffset.UtcNow,
                    Title: orphan.Title ?? string.Empty,
                    ErrorMessage: "宿主重启导致任务丢失",
                    FailureReason: "host_restart_orphan",
                    FailedAt: now));
            }

            publisher.Publish(Events.OnSystemNotice, new SystemNoticeArgs(
                Content: $"检测到 {orphans.Count} 个 Workflow 任务因上次宿主重启中断，已标记为失败。" +
                         "可在工作台查看详情或使用「复制为新任务」功能重新启动。",
                Title: "Workflow 任务恢复",
                Level: "warning",
                Source: "WorkflowExecutor",
                CreatedAt: DateTimeOffset.Now));

            logger.LogWarning("WorkflowExecutor 启动孤儿扫描：清理 {Count} 个遗留任务。", orphans.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WorkflowExecutor 启动孤儿扫描失败");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 取消所有运行中任务的 CTS，让执行循环走 OperationCanceledException 分支
        var snapshot = _runningTasks.Values.ToArray();
        if (snapshot.Length == 0) return;

        logger.LogInformation("WorkflowExecutor 关闭：取消 {Count} 个运行中任务。", snapshot.Length);

        foreach (var ctx in snapshot)
        {
            try { ctx.Cts.Cancel(); }
            catch (Exception ex) { logger.LogWarning(ex, "取消任务 {TaskId} 失败", ctx.TaskId); }
        }

        // 给执行循环最多 5 秒时间收尾
        var allTasks = snapshot.Select(c => c.ExecutionTask).ToArray();
        try
        {
            await Task.WhenAny(Task.WhenAll(allTasks), Task.Delay(5_000, cancellationToken));
        }
        catch (OperationCanceledException) { /* 关闭超时，强制返回 */ }
    }

    // ──── 任务生命周期 ────

    public Task<string> StartTaskAsync(WorkflowTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taskId = Guid.NewGuid().ToString("N");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var traceId = string.IsNullOrEmpty(request.TraceId)
            ? Guid.NewGuid().ToString("N")
            : request.TraceId;

        // 1. 写 OrchestrationTask 表（Pending → Running） + 参与者表
        var entity = InsertTaskAndParticipants(taskId, request, traceId, nowMs);

        // 2. 发 task.started 事件
        var participantIds = CollectParticipantIds(request);
        PublishTaskStarted(taskId, request, traceId, entity, participantIds, nowMs);

        // 3. 按 SubMode 分流：groupchat 走真实异步编排，其他保留同步占位
        if (string.Equals(request.SubMode, "groupchat", StringComparison.OrdinalIgnoreCase))
        {
            StartGroupChatBackground(taskId, request, traceId, entity, participantIds, nowMs, cancellationToken);
        }
        else
        {
            // 阶段 4B 起接入 magentic / parallelanalysis；当前保留 2B 占位行为
            CompletePlaceholderTask(taskId, request, traceId, entity, participantIds, nowMs);
        }

        logger.LogInformation(
            "Workflow 任务已创建：taskId={TaskId}, mode={Mode}, subMode={SubMode}, exec={ExecMode}",
            taskId, request.Mode, request.SubMode,
            string.Equals(request.SubMode, "groupchat", StringComparison.OrdinalIgnoreCase) ? "groupchat-async" : "placeholder-sync");

        return Task.FromResult(taskId);
    }

    public Task<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(taskId)) return Task.FromResult(false);

        // 1. 优先取消运行中任务的 CTS（GroupChat 异步路径）
        if (_runningTasks.TryGetValue(taskId, out var ctx))
        {
            try { ctx.Cts.Cancel(); }
            catch (Exception ex) { logger.LogWarning(ex, "取消任务 {TaskId} CTS 失败", taskId); }
            // 真实状态变化由执行循环的 OperationCanceledException 分支处理
            return Task.FromResult(true);
        }

        // 2. 不在内存中：可能是占位任务（已完成）或上一次宿主已经清理过的孤儿
        var task = taskRepo.GetById(taskId);
        if (task is null) return Task.FromResult(false);

        var status = WorkflowTaskStatusExtensions.FromDbValue(task.Status);
        if (status is WorkflowTaskStatus.Running or WorkflowTaskStatus.Paused or WorkflowTaskStatus.Pending)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            taskRepo.UpdateCompleted(
                taskId,
                status: WorkflowTaskStatus.Cancelled.ToDbValue(),
                finalReport: task.FinalReport,
                errorMessage: "用户取消",
                completedAt: now,
                lastActiveTimestamp: now);

            publisher.Publish(Events.OnWorkflowTaskFailed, new WorkflowTaskFailedArgs(
                TaskId: taskId,
                SourceSessionId: task.SourceSessionId,
                TraceId: task.TraceId,
                WorkspaceId: task.WorkspaceId,
                Mode: task.Mode,
                SubMode: task.SubMode,
                OccurredAt: DateTimeOffset.UtcNow,
                Title: task.Title ?? string.Empty,
                ErrorMessage: "用户取消",
                FailureReason: "cancelled",
                FailedAt: now));

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<WorkflowTaskStatus> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = taskRepo.GetById(taskId);
        return Task.FromResult(task is null
            ? WorkflowTaskStatus.Failed
            : WorkflowTaskStatusExtensions.FromDbValue(task.Status));
    }

    // ──── 任务列表与浏览 ────

    public Task<IReadOnlyList<OrchestrationTaskEntity>> ListTasksAsync(
        WorkflowTaskListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var list = taskRepo.ListByWorkspace(
            query.WorkspaceId,
            query.IncludeArchived,
            query.Statuses,
            query.Limit,
            query.Offset);
        return Task.FromResult<IReadOnlyList<OrchestrationTaskEntity>>(list);
    }

    public Task<OrchestrationTaskDetail?> GetTaskDetailAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = taskRepo.GetById(taskId);
        if (task is null) return Task.FromResult<OrchestrationTaskDetail?>(null);

        var status = WorkflowTaskStatusExtensions.FromDbValue(task.Status);

        var detail = new OrchestrationTaskDetail
        {
            Task = task,
            Participants = stepRepo.GetParticipantsByTask(taskId),
            Steps = stepRepo.GetStepsByTask(taskId),
            Messages = stepRepo.GetMessagesByTask(taskId),
            IsLive = status is WorkflowTaskStatus.Running or WorkflowTaskStatus.Paused,
            CanCancel = status is WorkflowTaskStatus.Running or WorkflowTaskStatus.Paused or WorkflowTaskStatus.Pending,
            CanDuplicate = status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Failed or WorkflowTaskStatus.Cancelled,
        };
        return Task.FromResult<OrchestrationTaskDetail?>(detail);
    }

    // ──── 元数据操作 ────

    public Task SetPinnedAsync(string taskId, bool pinned, CancellationToken cancellationToken)
    {
        taskRepo.SetPinned(taskId, pinned);
        return Task.CompletedTask;
    }

    public Task SetArchivedAsync(string taskId, bool archived, CancellationToken cancellationToken)
    {
        taskRepo.SetArchived(taskId, archived);
        return Task.CompletedTask;
    }

    public Task RenameTitleAsync(string taskId, string newTitle, CancellationToken cancellationToken)
    {
        var task = taskRepo.GetById(taskId);
        if (task is null) return Task.CompletedTask;

        var oldTitle = task.Title ?? string.Empty;
        taskRepo.UpdateTitle(taskId, newTitle ?? string.Empty, isAutoGenerated: false);

        publisher.Publish(Events.OnWorkflowTaskTitleUpdated, new WorkflowTaskTitleUpdatedArgs(
            TaskId: taskId,
            SourceSessionId: task.SourceSessionId,
            TraceId: task.TraceId,
            WorkspaceId: task.WorkspaceId,
            Mode: task.Mode,
            SubMode: task.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            OldTitle: oldTitle,
            NewTitle: newTitle ?? string.Empty,
            IsAutoGenerated: false));

        return Task.CompletedTask;
    }

    public Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        taskRepo.Delete(taskId);
        return Task.CompletedTask;
    }

    // ──── 复制为新任务（决策 10-A） ────

    public Task<WorkflowTaskRequest> BuildRequestFromTemplateAsync(
        string sourceTaskId,
        CancellationToken cancellationToken)
    {
        var source = taskRepo.GetById(sourceTaskId)
            ?? throw new InvalidOperationException($"Source task not found: {sourceTaskId}");

        var participants = stepRepo.GetParticipantsByTask(sourceTaskId);
        var memberIds = participants
            .Where(p => p.Role == "member")
            .Select(p => p.AgentId)
            .ToList();

        return Task.FromResult(new WorkflowTaskRequest
        {
            Title = null,
            Summary = source.Summary,
            Mode = source.Mode,
            SubMode = source.SubMode,
            InitialInput = source.InitialInput,
            InitialAttachmentsJson = source.InitialAttachmentsJson,
            WorkspaceId = source.WorkspaceId,
            CreatedBy = "duplicated",
            SourceSessionId = source.SourceSessionId,
            SourceTaskId = sourceTaskId,
            TraceId = null,
            ManagerAgentId = source.ManagerAgentId,
            ManagerAgentName = source.ManagerAgentName,
            MemberAgentIds = memberIds,
            OverridesJson = source.OverridesJson,
        });
    }

    // ──────────────────────────────────────────────────────────────
    //  内部辅助：写库 / 发事件 / 占位完成（保持与阶段 2B 行为一致）
    // ──────────────────────────────────────────────────────────────

    private OrchestrationTaskEntity InsertTaskAndParticipants(
        string taskId, WorkflowTaskRequest request, string traceId, long nowMs)
    {
        var entity = new OrchestrationTaskEntity
        {
            Id = taskId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? string.Empty : request.Title!,
            Summary = request.Summary ?? string.Empty,
            IsTitleAutoGenerated = string.IsNullOrWhiteSpace(request.Title),
            Mode = request.Mode,
            SubMode = request.SubMode,
            Status = WorkflowTaskStatus.Running.ToDbValue(),
            StartedAt = nowMs,
            LastActiveTimestamp = nowMs,
            TraceId = traceId,
            CreatedBy = request.CreatedBy,
            SourceSessionId = request.SourceSessionId,
            SourceTaskId = request.SourceTaskId,
            ManagerAgentId = request.ManagerAgentId,
            ManagerAgentName = request.ManagerAgentName,
            InitialInput = request.InitialInput,
            InitialAttachmentsJson = request.InitialAttachmentsJson,
            WorkspaceId = request.WorkspaceId,
            OverridesJson = request.OverridesJson,
        };
        taskRepo.Insert(entity);

        if (!string.IsNullOrEmpty(request.ManagerAgentId))
        {
            stepRepo.InsertParticipant(new OrchestrationParticipantEntity
            {
                TaskId = taskId,
                AgentId = request.ManagerAgentId!,
                AgentName = request.ManagerAgentName ?? string.Empty,
                Role = "manager",
                JoinedAt = nowMs,
            });
        }
        foreach (var memberId in request.MemberAgentIds)
        {
            stepRepo.InsertParticipant(new OrchestrationParticipantEntity
            {
                TaskId = taskId,
                AgentId = memberId,
                AgentName = string.Empty,
                Role = "member",
                JoinedAt = nowMs,
            });
        }

        return entity;
    }

    private static List<string> CollectParticipantIds(WorkflowTaskRequest request)
    {
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(request.ManagerAgentId)) ids.Add(request.ManagerAgentId!);
        ids.AddRange(request.MemberAgentIds);
        return ids;
    }

    private void PublishTaskStarted(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long nowMs)
    {
        publisher.Publish(Events.OnWorkflowTaskStarted, new WorkflowTaskStartedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            ManagerAgentId: request.ManagerAgentId,
            ManagerAgentName: request.ManagerAgentName,
            InitialInput: request.InitialInput,
            ParticipantAgentIds: participantIds,
            StartedAt: nowMs));
    }

    /// <summary>
    /// 阶段 2B 占位完成路径：立即 UPDATE Completed + 发 task.completed。
    /// 阶段 4B 接入 magentic / parallelanalysis 后，相关分支会切换到独立的异步执行路径。
    /// </summary>
    private void CompletePlaceholderTask(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs)
    {
        const string placeholderReport =
            "[阶段 3B 占位] 该子模式尚未接入真实编排，本任务仅打通基础设施。\n\n" +
            "GroupChat 子模式已在阶段 3B 接入；Magentic / ParallelAnalysis 等将在阶段 4B 起接入。";

        var completedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        taskRepo.UpdateCompleted(
            taskId,
            status: WorkflowTaskStatus.Completed.ToDbValue(),
            finalReport: placeholderReport,
            errorMessage: null,
            completedAt: completedAtMs,
            lastActiveTimestamp: completedAtMs);

        publisher.Publish(Events.OnWorkflowTaskCompleted, new WorkflowTaskCompletedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            ManagerAgentId: request.ManagerAgentId,
            ManagerAgentName: request.ManagerAgentName,
            FinalReport: placeholderReport,
            StepCount: 0,
            TotalDurationMs: completedAtMs - startedMs,
            TotalTokenInputCount: 0,
            TotalTokenOutputCount: 0,
            ParticipantAgentIds: participantIds,
            CompletedAt: completedAtMs));

        // 占位不调用真实 LLM，所以也不触发标题兜底
        _ = titleGenerator;
        _ = options;
    }

    // ──────────────────────────────────────────────────────────────
    //  GroupChat 真实异步执行（阶段 3B 新增）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动 GroupChat 后台执行循环。立即返回（不等待），任务进入 _runningTasks 表。
    /// </summary>
    private void StartGroupChatBackground(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        CancellationToken externalCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        cts.CancelAfter(options.TaskTimeout);

        var executionTask = Task.Run(async () =>
        {
            try
            {
                await RunGroupChatAsync(taskId, request, traceId, entity, participantIds, startedMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                HandleTaskCancelled(taskId, request, traceId, entity, participantIds, startedMs);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GroupChat 任务 {TaskId} 执行失败", taskId);
                HandleTaskFailed(taskId, request, traceId, entity, participantIds, startedMs, ex);
            }
            finally
            {
                if (_runningTasks.TryRemove(taskId, out var ctx))
                {
                    ctx.Dispose();
                }
            }
        }, CancellationToken.None);  // 用 None 避免 Task.Run 自身被取消，由 cts 控制内部循环

        _runningTasks[taskId] = new RunningTaskContext(taskId, executionTask, cts, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// GroupChat 任务执行主循环：构建 workflow → 启动 → 消费事件 → 写库 + 发事件。
    /// </summary>
    private async Task RunGroupChatAsync(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        CancellationToken ct)
    {
        // 1. 加载并构建参与者（Manager + Members 全部走轻量路径）
        var participants = LoadAndBuildParticipants(request);
        if (participants.Count == 0)
        {
            HandleTaskFailed(taskId, request, traceId, entity, participantIds, startedMs,
                new InvalidOperationException("GroupChat 任务无有效参与者，请检查 ManagerAgentId / MemberAgentIds 配置"));
            return;
        }

        // 2. 构建 SDK Workflow
        var workflow = GroupChatWorkflowFactory.Build(participants, taskId, options, logger);

        // 3. 启动并消费事件流。input 为 List<ChatMessage>（参考 SDK GroupChatToolApproval sample）。
        var input = new List<ChatMessage> { new(ChatRole.User, request.InitialInput) };
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, sessionId: null, ct);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var stepIndex = 0;
        var stepCount = 0;
        string? finalReport = null;

        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            switch (evt)
            {
                case ExecutorCompletedEvent completed:
                    stepCount++;
                    PersistAndPublishStep(taskId, request, traceId, completed, ++stepIndex);
                    break;

                case AgentResponseEvent agentResponse:
                    // 一个 agent 的整轮回复：更新 finalReport 候选（取最后一个）
                    finalReport = agentResponse.Response.Text;
                    break;

                case WorkflowOutputEvent output when finalReport is null:
                    // 兜底：尝试从 Output 里提取
                    finalReport = ExtractFinalReportFromOutput(output);
                    break;
            }
        }

        // 4. 完成：写库 + 发 task.completed + 异步触发标题兜底
        // 传第一个 participant 作为标题生成参考 agent（用于解析压缩模型 ChatClient）
        HandleTaskCompleted(taskId, request, traceId, entity, participantIds, startedMs, stepCount, finalReport, participants[0]);
    }

    /// <summary>
    /// 加载 request 中的 ManagerAgent + Member Agents 并构建为 AIAgent。
    /// </summary>
    private IReadOnlyList<AIAgent> LoadAndBuildParticipants(WorkflowTaskRequest request)
    {
        var entities = new List<AgentEntity>();

        // Manager 优先
        if (!string.IsNullOrEmpty(request.ManagerAgentId))
        {
            var manager = agentService.GetById(request.ManagerAgentId);
            if (manager is not null) entities.Add(manager);
        }

        // 然后 Members
        foreach (var memberId in request.MemberAgentIds)
        {
            var member = agentService.GetById(memberId);
            if (member is not null) entities.Add(member);
        }

        if (entities.Count == 0)
        {
            return [];
        }

        // 取第一个 agent 自身配置的 provider/model 作为 fallback；都没配则取全表第一个可用的。
        AiProviderEntity? fallbackProvider = null;
        AiModelEntity? fallbackModel = null;
        foreach (var ent in entities)
        {
            if (fallbackProvider is null && !string.IsNullOrEmpty(ent.DefaultProviderId))
                fallbackProvider = providerService.GetById(ent.DefaultProviderId);
            if (fallbackModel is null && !string.IsNullOrEmpty(ent.DefaultModelId))
                fallbackModel = modelService.GetById(ent.DefaultModelId);
            if (fallbackProvider is not null && fallbackModel is not null) break;
        }

        fallbackProvider ??= providerService.GetAll().FirstOrDefault(p => p.IsEnabled);
        if (fallbackProvider is null) return [];

        fallbackModel ??= modelService.GetByProviderId(fallbackProvider.Id).FirstOrDefault(m => m.IsEnabled);
        if (fallbackModel is null) return [];

        return factory.BuildWorkflowParticipants(
            entities, fallbackProvider, fallbackModel, providerService, modelService);
    }

    /// <summary>
    /// 从 WorkflowOutputEvent.Data 里尝试提取最终回复文本。
    /// </summary>
    private static string? ExtractFinalReportFromOutput(WorkflowOutputEvent output)
    {
        if (output.Data is AgentResponse resp) return resp.Text;
        if (output.Data is List<ChatMessage> messages)
        {
            return messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
        }
        if (output.Data is ChatMessage single) return single.Text;
        return output.Data?.ToString();
    }

    /// <summary>
    /// 写一条 OrchestrationStep 记录 + 发 step.completed 事件。
    /// </summary>
    private void PersistAndPublishStep(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        ExecutorCompletedEvent completed,
        int sequence)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stepId = Guid.NewGuid().ToString("N");
        var resultText = completed.Data?.ToString() ?? string.Empty;

        var stepEntity = new OrchestrationStepEntity
        {
            Id = stepId,
            TaskId = taskId,
            ParentStepId = null,
            Sequence = sequence,
            AgentId = completed.ExecutorId,
            AgentName = completed.ExecutorId,
            Action = "speak",
            Status = "completed",
            StartedAt = nowMs,
            CompletedAt = nowMs,
            DurationMs = 0,
            TokenInputCount = 0,
            TokenOutputCount = 0,
            ErrorMessage = null,
            SummaryJson = resultText,
        };

        try
        {
            stepRepo.InsertStep(stepEntity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 OrchestrationStep 失败：taskId={TaskId}, stepId={StepId}", taskId, stepId);
        }

        publisher.Publish(Events.OnWorkflowStepCompleted, new WorkflowStepCompletedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            StepId: stepId,
            ParentStepId: null,
            Sequence: sequence,
            AgentId: completed.ExecutorId,
            AgentName: completed.ExecutorId,
            Action: "speak",
            Status: "completed",
            StartedAt: nowMs,
            CompletedAt: nowMs,
            DurationMs: 0,
            TokenInputCount: 0,
            TokenOutputCount: 0,
            ErrorMessage: null,
            SummaryJson: resultText));
    }

    private void HandleTaskCompleted(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        int stepCount,
        string? finalReport,
        AIAgent referenceAgent)
    {
        var completedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var report = string.IsNullOrEmpty(finalReport)
            ? "[GroupChat 已完成，但未产出最终回复]"
            : finalReport;

        taskRepo.UpdateCompleted(
            taskId,
            status: WorkflowTaskStatus.Completed.ToDbValue(),
            finalReport: report,
            errorMessage: null,
            completedAt: completedAtMs,
            lastActiveTimestamp: completedAtMs);

        publisher.Publish(Events.OnWorkflowTaskCompleted, new WorkflowTaskCompletedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            ManagerAgentId: request.ManagerAgentId,
            ManagerAgentName: request.ManagerAgentName,
            FinalReport: report,
            StepCount: stepCount,
            TotalDurationMs: completedAtMs - startedMs,
            TotalTokenInputCount: 0,
            TotalTokenOutputCount: 0,
            ParticipantAgentIds: participantIds,
            CompletedAt: completedAtMs));

        // 异步触发标题兜底（决策 6-A）：原 title 为空时由 LLM 生成
        if (entity.IsTitleAutoGenerated && string.IsNullOrEmpty(entity.Title))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await titleGenerator.GenerateAndUpdateTitleAsync(
                        referenceAgent, taskId, request.InitialInput, report, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "GroupChat 任务 {TaskId} 标题兜底生成失败", taskId);
                }
            }, CancellationToken.None);
        }
    }

    private void HandleTaskCancelled(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        taskRepo.UpdateCompleted(
            taskId,
            status: WorkflowTaskStatus.Cancelled.ToDbValue(),
            finalReport: null,
            errorMessage: "用户取消",
            completedAt: nowMs,
            lastActiveTimestamp: nowMs);

        publisher.Publish(Events.OnWorkflowTaskFailed, new WorkflowTaskFailedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            ErrorMessage: "用户取消",
            FailureReason: "cancelled",
            FailedAt: nowMs));

        _ = participantIds;
        _ = startedMs;
    }

    private void HandleTaskFailed(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        Exception ex)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        taskRepo.UpdateCompleted(
            taskId,
            status: WorkflowTaskStatus.Failed.ToDbValue(),
            finalReport: null,
            errorMessage: ex.Message,
            completedAt: nowMs,
            lastActiveTimestamp: nowMs);

        publisher.Publish(Events.OnWorkflowTaskFailed, new WorkflowTaskFailedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            ErrorMessage: ex.Message,
            FailureReason: "execution_error",
            FailedAt: nowMs));

        _ = participantIds;
        _ = startedMs;
    }
}
