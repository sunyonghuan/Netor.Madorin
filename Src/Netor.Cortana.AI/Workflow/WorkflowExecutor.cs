using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.Workflow.Builders;
using Netor.Cortana.AI.Workflow.Title;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

// SDK 的 Workflow 类型名与本项目 namespace `Netor.Cortana.AI.Workflow` 同名，
// 编译器优先解析为命名空间，因此用类型别名消除歧义（与 GroupChatWorkflowFactory / MagenticWorkflowFactory 一致）。
using SdkWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// 默认 <see cref="IWorkflowExecutor"/> 实现。
///
/// 阶段 2B 占位：仅打通数据库 + 事件路径，不接入真实 SDK Workflow。
/// 阶段 3B 升级：
/// - SubMode = "groupchat" 走 SDK 真实编排（<see cref="GroupChatWorkflowFactory"/> + <see cref="InProcessExecution"/>）
/// - SubMode = 其他保留占位实现（推到阶段 4B 接入 magentic / parallelanalysis 等）
/// 阶段 4B 升级：
/// - SubMode = "magentic" 走 SDK <see cref="MagenticWorkflowFactory"/>（Manager 规划 + 团队执行 + 自动重规划）
/// - SubMode = "parallelanalysis" 走 SDK <see cref="ParallelAnalysisWorkflowFactory"/>（并发执行 + Markdown 合并）
/// - 真实编排白名单见 <see cref="IsRealWorkflowSubMode"/>；其他 SubMode 仍走占位路径
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
    WorkflowCheckpointRepository checkpointRepo,
    WorkflowExecutorOptions options,
    IWorkflowTitleGenerator titleGenerator,
    AIAgentFactory factory,
    AgentService agentService,
    AiProviderService providerService,
    AiModelService modelService,
    SystemSettingsService systemSettings,
    CheckpointManager checkpointManager,
    Netor.Cortana.AI.Workflow.DynamicAgents.DynamicAgentRegistry dynamicAgentRegistry,
    Netor.Cortana.AI.Workflow.DynamicAgents.DynamicAgentCreationGate dynamicAgentCreationGate,
    IPublisher publisher,
    ILogger<WorkflowExecutor> logger) : IWorkflowExecutor, IHostedService
{
    /// <summary>
    /// 运行中任务追踪表。键 = taskId，值 = 任务运行期上下文。
    /// 当 GroupChat / Magentic / ParallelAnalysis 等真实编排子模式启动后台执行循环时，向此表插入条目；
    /// 任务正常结束 / 异常 / 取消时从此表移除。
    /// </summary>
    private readonly ConcurrentDictionary<string, RunningTaskContext> _runningTasks = new();

    /// <summary>
    /// HITL（人在回路）暂停任务追踪表。键 = taskId，值 = HITL 等待上下文。
    /// 事件循环收到 <see cref="RequestInfoEvent"/> → 写入此表 → 阻塞等待 <c>Tcs</c>；
    /// 外部调用 <c>ResumeAsync</c> 解锁 Tcs → 事件循环恢复 → 从此表移除。
    /// 阶段 5B 引入（决策 5B-A）。
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingHitlContext> _pausedTasks = new();

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
            var checkpointCleanedCount = 0;
            foreach (var orphan in orphans)
            {
                // 阶段 5B：跨进程 Checkpoint 恢复延后到下一阶段（[04] §5B.2 注解：仅支持宿主进程内重启）。
                // 当前实现：mark failed + 清理该任务的 Checkpoint BLOB（避免长期堆积，决策 5B-C）。
                try
                {
                    var deleted = checkpointRepo.DeleteByTaskId(orphan.Id);
                    if (deleted > 0)
                    {
                        checkpointCleanedCount += deleted;
                        logger.LogDebug("孤儿任务 {TaskId} 清理 {Count} 个 Checkpoint", orphan.Id, deleted);
                    }
                }
                catch (Exception cpEx)
                {
                    logger.LogWarning(cpEx, "孤儿任务 {TaskId} Checkpoint 清理失败（不影响 mark failed）", orphan.Id);
                }

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

            logger.LogWarning(
                "WorkflowExecutor 启动孤儿扫描：清理 {Count} 个遗留任务，{CpCount} 个 Checkpoint。",
                orphans.Count, checkpointCleanedCount);
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

        // 3. 按 SubMode 分流：白名单内的真实编排子模式走异步执行，其他走同步占位
        var isReal = IsRealWorkflowSubMode(request.SubMode);
        if (isReal)
        {
            StartRealWorkflowBackground(taskId, request, traceId, entity, participantIds, nowMs, cancellationToken);
        }
        else
        {
            // 未识别 SubMode：保留 2B 占位行为（即时完成 + 占位 FinalReport）
            CompletePlaceholderTask(taskId, request, traceId, entity, participantIds, nowMs);
        }

        logger.LogInformation(
            "Workflow 任务已创建：taskId={TaskId}, mode={Mode}, subMode={SubMode}, exec={ExecMode}",
            taskId, request.Mode, request.SubMode,
            isReal ? $"{request.SubMode}-async" : "placeholder-sync");

        return Task.FromResult(taskId);
    }

    /// <summary>
    /// 真实编排子模式白名单。仅以下 SubMode 走 SDK 异步执行路径，其他 SubMode 走占位。
    /// 比对忽略大小写，与 <see cref="StartRealWorkflowBackground"/> 内的 switch 子模式串保持小写一致。
    /// </summary>
    private static bool IsRealWorkflowSubMode(string? subMode)
        => subMode?.Trim().ToLowerInvariant() switch
        {
            "groupchat" or "magentic" or "parallelanalysis" => true,
            _ => false,
        };

    public Task<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(taskId)) return Task.FromResult(false);

        // 1. 优先取消运行中任务的 CTS（GroupChat / Magentic / ParallelAnalysis 等真实编排异步路径）
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
            query.Offset,
            query.Keyword,   // 阶段 6 Phase 3：标题搜索关键词透传
            query.SubModes); // P1 群聊真实化：SubMode 过滤透传（收尾决策 DT-9）
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
        // 阶段 6 Phase 2：把 request.ToolBlacklist (List<string>) 序列化为 JSON 数组（AOT 安全）。
        // 用 JsonArray 而不是 JsonSerializer.Serialize（避免反射式序列化 + 不依赖 JsonContext source-gen）。
        // 详见 04-实施阶段.md §阶段 6 #1。
        string? toolBlacklistJson = null;
        if (request.ToolBlacklist is { Count: > 0 } list)
        {
            var arr = new JsonArray();
            foreach (var item in list)
            {
                if (!string.IsNullOrWhiteSpace(item))
                    arr.Add(item);
            }
            if (arr.Count > 0)
                toolBlacklistJson = arr.ToJsonString();
        }

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
            ToolBlacklistJson = toolBlacklistJson,   // 阶段 6 Phase 2
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
    /// 阶段 3B 接入 GroupChat、阶段 4B 接入 Magentic / ParallelAnalysis 之后，
    /// 该方法仅用于 SubMode 不在白名单内（<see cref="IsRealWorkflowSubMode"/>）的未来扩展 / 错误配置 / 兼容场景。
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
            "[占位] 该 SubMode 未被任何真实编排工厂支持，本任务仅打通基础设施。\n\n" +
            "当前已接入：GroupChat（3B）、Magentic / ParallelAnalysis（4B）；其他 SubMode 需要在 IsRealWorkflowSubMode 白名单内显式注册并提供对应工厂。";

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
            CompletedAt: completedAtMs,
            AllowMemoryIngest: ResolveAllowMemoryIngest(request.ManagerAgentId)));

        // 占位不调用真实 LLM，所以也不触发标题兜底
        _ = titleGenerator;
        _ = options;
    }

    // ──────────────────────────────────────────────────────────────
    //  真实编排异步执行（阶段 3B 引入 GroupChat；阶段 4B 扩展 Magentic / ParallelAnalysis）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动真实 Workflow 后台执行循环。立即返回（不等待），任务进入 <see cref="_runningTasks"/> 表。
    /// 支持 GroupChat / Magentic / ParallelAnalysis 等 SubMode（白名单见 <see cref="IsRealWorkflowSubMode"/>），
    /// 具体 SDK Workflow 在 <see cref="RunWorkflowAsync"/> 内按 SubMode 选择工厂构建。
    /// </summary>
    private void StartRealWorkflowBackground(
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
                await RunWorkflowAsync(taskId, request, traceId, entity, participantIds, startedMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                HandleTaskCancelled(taskId, request, traceId, entity, participantIds, startedMs);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workflow 任务 {TaskId} (SubMode={SubMode}) 执行失败", taskId, request.SubMode);
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
    /// 真实编排任务执行主循环：构建 workflow → 启动 → 消费事件 → 写库 + 发事件。
    /// SubMode 决定使用哪个工厂构建 SDK <see cref="SdkWorkflow"/>，事件循环对所有子模式一致。
    /// </summary>
    private async Task RunWorkflowAsync(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        CancellationToken ct)
    {
        try
        {
            // 1. 加载并构建参与者（Manager + Members 全部走轻量路径）
            // 阶段 6 Phase 1：维护 trackers 字典让 PersistAndPublishStep 在 step 完成时反查 token；
            // outputSnapshot 字典记录每个 agent 的累计输出快照，用于计算 step 级 output 增量。
            // 阶段 6 Phase 2：把 request.ToolBlacklist 传给参与者构建，AssembleToolProviders 在工具组装阶段过滤掉黑名单工具。
            // P2-2：解析 OverridesJson 中的 maxSubAgents（默认 5，与 WorkflowInputVm.MaxSubAgents 一致），
            //       并把 taskId 透传给 LoadAndBuildParticipants → BuildWorkflowParticipants → BuildSubAgent，
            //       让 Manager 注入动态子智能体能力（DynamicAgentToolsProvider + create_subagent 工具）。
            var trackers = new Dictionary<string, TokenTrackingChatClient>(StringComparer.OrdinalIgnoreCase);
            var outputSnapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var maxSubAgents = ParseMaxSubAgentsFromOverrides(request.OverridesJson);
            var participants = LoadAndBuildParticipants(
                request, trackers, request.ToolBlacklist, taskId, maxSubAgents);
            if (participants.Count == 0)
            {
                HandleTaskFailed(taskId, request, traceId, entity, participantIds, startedMs,
                    new InvalidOperationException(
                        $"Workflow 任务（SubMode={request.SubMode}）无有效参与者，请检查 ManagerAgentId / MemberAgentIds 配置"));
                return;
            }

            // 2. 按 SubMode 选择工厂构建 SDK Workflow（白名单已在 IsRealWorkflowSubMode 内确认）
            SdkWorkflow workflow = request.SubMode?.Trim().ToLowerInvariant() switch
            {
                "groupchat"        => GroupChatWorkflowFactory.Build(participants, taskId, options, logger),
                "magentic"         => MagenticWorkflowFactory.Build(participants, taskId, options, logger),
                "parallelanalysis" => ParallelAnalysisWorkflowFactory.Build(participants, taskId, logger),
                _ => throw new InvalidOperationException(
                    $"未知 SubMode：{request.SubMode}（已通过白名单但未匹配工厂分支，疑似 IsRealWorkflowSubMode 与 switch 不一致）"),
            };

            // 3. 启动并消费事件流。input 为 List<ChatMessage>（参考 SDK GroupChatToolApproval sample）。
            //    阶段 5B 起注入 CheckpointManager + sessionId=taskId（决策 5B-C），SDK 在 superstep 边界自动写 Checkpoint，
            //    供 StartAsync 启动孤儿扫描尝试 RestoreCheckpoint 恢复（详见 §5B.2）。
            var input = new List<ChatMessage> { new(ChatRole.User, request.InitialInput) };
            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow, input, checkpointManager, sessionId: taskId, ct);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            var stepIndex = 0;
            var stepCount = 0;
            long totalTokens = 0;   // 阶段 6 Phase 1：累计任务 token（input + output）
            string? finalReport = null;

            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                ct.ThrowIfCancellationRequested();

                switch (evt)
                {
                    case ExecutorCompletedEvent completed:
                        stepCount++;
                        var (stepIn, stepOut) = PersistAndPublishStep(taskId, request, traceId, completed, ++stepIndex, trackers, outputSnapshot);
                        totalTokens += stepIn + stepOut;
                        break;

                    case AgentResponseEvent agentResponse:
                        // P3-1 修复 2026-05-24：AgentResponseEvent 也持久化为时间线步骤。
                        // SDK Magentic 模式下，每个子智能体的回复都会触发此事件。
                        // 之前仅用于更新 finalReport 候选，导致时间线只显示 1 条 ExecutorCompletedEvent。
                        // 现在：(1) 每条 AgentResponseEvent 写入步骤表 + 发 step.completed 事件
                        //       (2) 非空文本仍更新 finalReport 候选
                        {
                            var responseText = agentResponse.Response.Text;
                            var agentId = agentResponse.ExecutorId ?? "unknown";

                            // 构建结构化 SummaryJson（让 WorkflowTimelineStepVm.ParseSummaryContent 可解析）
                            var summaryJson = BuildAgentResponseSummaryJson(agentResponse);

                            // 持久化为时间线步骤
                            var (arStepIn, arStepOut) = PersistAndPublishAgentResponseStep(
                                taskId, request, traceId, agentId, summaryJson, ++stepIndex, trackers, outputSnapshot);
                            stepCount++;
                            totalTokens += arStepIn + arStepOut;

                            // 更新 finalReport 候选（仅非空时）
                            if (!string.IsNullOrWhiteSpace(responseText))
                            {
                                finalReport = responseText;
                            }
                        }
                        break;

                    case WorkflowOutputEvent output:
                        // P2-2-Bug-E 修复 2026-05-17：去掉 `when finalReport is null` 守卫（之前会跳过 SDK 真正的最终输出）。
                        // SDK MagenticOrchestrator.PrepareFinalAnswerAsync 通过 YieldOutputAsync 输出 List<ChatMessage>，
                        // 这才是 Magentic 真正的 FinalAnswer，应该最高优先级覆盖之前 AgentResponseEvent 记录的候选。
                        // 详见 SDK Microsoft.Agents.AI.Workflows/Specialized/Magentic/MagenticOrchestrator.cs §297-300。
                        var extracted = ExtractFinalReportFromOutput(output);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            finalReport = extracted;
                        }
                        break;

                    case RequestInfoEvent requestInfo:
                        // 阶段 5B：HITL（人在回路）。Magentic RequirePlanSignoff(true) 触发；
                        // 阻塞等待用户通过 ResumeAsync 解锁后回写 ExternalResponse 给 SDK。
                        await HandleHitlRequestAsync(taskId, request, traceId, entity, run, requestInfo, ct);
                        break;
                }
            }

            // 4. 完成：写库 + 发 task.completed + 异步触发标题兜底
            // 传第一个 participant 作为标题生成参考 agent（用于解析压缩模型 ChatClient）
            HandleTaskCompleted(taskId, request, traceId, entity, participantIds, startedMs, stepCount, totalTokens, finalReport, participants[0]);
        }
        finally
        {
            // P2-2：任务结束（成功 / 失败 / 取消）时统一清理动态子智能体 Registry，避免内存泄漏。
            // ClearTask 是幂等的：如果该任务从未注册 dynamic agent（CountByTask==0），TryRemove 失败也无影响。
            dynamicAgentRegistry.ClearTask(taskId);

            // P2-4：同步清理审批闸（取消该任务下所有 pending 等待 + 移除 auto-approve 标志）。
            // 顺序无关：Gate.ClearTask 也是幂等的。
            dynamicAgentCreationGate.ClearTask(taskId);
        }
    }

    /// <summary>
    /// P2-2：从 <c>OverridesJson</c> 解析 <c>maxSubAgents</c>（Manager 最多创建几个动态子智能体）。
    /// 默认 5，与 <c>WorkflowInputVm.MaxSubAgents</c> 默认值一致。
    /// </summary>
    /// <remarks>
    /// 期望的 OverridesJson 形态（可空）：
    /// <code>{ "maxSubAgents": 5, "MaxRounds": 8, ... }</code>
    /// 解析失败 / 字段不存在 / 值非法时返回默认值 5（兜底，不抛异常）。
    /// </remarks>
    private int ParseMaxSubAgentsFromOverrides(string? overridesJson)
    {
        const int defaultValue = 5;
        if (string.IsNullOrWhiteSpace(overridesJson)) return defaultValue;

        try
        {
            using var doc = JsonDocument.Parse(overridesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return defaultValue;

            // 兼容大小写两种字段名（"maxSubAgents" / "MaxSubAgents"）
            if (doc.RootElement.TryGetProperty("maxSubAgents", out var v1) ||
                doc.RootElement.TryGetProperty("MaxSubAgents", out v1))
            {
                if (v1.ValueKind == JsonValueKind.Number && v1.TryGetInt32(out var n) && n > 0 && n <= 20)
                    return n;
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "OverridesJson 解析失败，使用默认 maxSubAgents={Default}", defaultValue);
        }

        return defaultValue;
    }

    /// <summary>
    /// 加载 request 中的 ManagerAgent + Member Agents 并构建为 AIAgent。
    /// 阶段 6 Phase 1：可选输出 trackerByAgentId 字典，让调用方在 step 完成时取 token 数据。
    /// 阶段 6 Phase 2：可选 taskBlacklist 参数，按 "pluginId:toolName" 在工具组装阶段过滤掉本次任务屏蔽的高风险工具。
    /// P2-2：可选 taskId / maxSubAgents 参数，<see cref="AIAgentFactory.BuildWorkflowParticipants"/> 会为第一个参与者
    /// （Manager）注入动态子智能体能力（DynamicAgentToolsProvider + create_subagent 工具）。
    /// </summary>
    private IReadOnlyList<AIAgent> LoadAndBuildParticipants(
        WorkflowTaskRequest request,
        IDictionary<string, TokenTrackingChatClient>? trackerByAgentId = null,
        IReadOnlyCollection<string>? taskBlacklist = null,
        string? taskId = null,
        int maxSubAgents = 5)
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
            entities, fallbackProvider, fallbackModel, providerService, modelService,
            trackerByAgentId, taskBlacklist, taskId, maxSubAgents,
            // P2-2 修复 2026-05-17：透传用户 UI 选的 Provider/Model（覆盖 Manager Agent.DefaultXxxId）
            // 决策 D-甲：仅作用于 Manager + 动态子智能体（详见 WorkflowTaskRequest.OverrideProviderId 注释）。
            request.OverrideProviderId, request.OverrideModelId);
    }

    /// <summary>
    /// 从 WorkflowOutputEvent.Data 里尝试提取最终回复文本。
    /// </summary>
    /// <summary>
    /// P3-1 改进 2026-05-24：从 WorkflowOutputEvent.Data 里尝试提取最终回复文本。
    /// 增加 IList / IEnumerable 兼容（SDK 可能返回不同集合类型），
    /// 并过滤 raw type name fallback（避免把 "System.Collections.Generic.List`1[...]" 当作 finalReport）。
    /// </summary>
    private static string? ExtractFinalReportFromOutput(WorkflowOutputEvent output)
    {
        if (output.Data is AgentResponse resp) return resp.Text;
        // 兼容 List<ChatMessage> / IList<ChatMessage> / IEnumerable<ChatMessage>
        if (output.Data is IEnumerable<ChatMessage> messages)
        {
            return messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
        }
        if (output.Data is ChatMessage single) return single.Text;

        // fallback：仅当 .ToString() 产出有意义的文本时使用（过滤掉 raw type name）
        var raw = output.Data?.ToString();
        if (!string.IsNullOrWhiteSpace(raw)
            && !raw.StartsWith("System.", StringComparison.Ordinal)
            && !raw.StartsWith("Microsoft.", StringComparison.Ordinal))
        {
            return raw;
        }
        return null;
    }

    /// <summary>
    /// 写一条 OrchestrationStep 记录 + 发 step.completed 事件。
    /// </summary>
    /// <summary>
    /// 阶段 6 Phase 1：返回本步骤的 (input, output) token 数，让 RunWorkflowAsync 累加成 task 级 TotalTokenCount。
    /// </summary>
    private (int InputTokens, int OutputTokens) PersistAndPublishStep(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        ExecutorCompletedEvent completed,
        int sequence,
        IReadOnlyDictionary<string, TokenTrackingChatClient> trackers,
        IDictionary<string, long> outputSnapshot)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stepId = Guid.NewGuid().ToString("N");
        // P3-1 修复 2026-05-24：改进 SummaryJson 格式 — 尝试结构化输出而非 raw .ToString()
        var resultText = BuildExecutorCompletedSummaryJson(completed);

        // 阶段 6 Phase 1：从对应 agent 的 TokenTrackingChatClient 读取 token 数据。
        // - LastInputTokens 直接是本步骤调用的输入 token（每次 LLM 调用都被覆盖）
        // - TotalOutputTokens 是累计输出，需要减去上一次快照得到本步骤的输出增量
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #2。
        var stepInputTokens = 0;
        var stepOutputTokens = 0;
        if (!string.IsNullOrEmpty(completed.ExecutorId)
            && trackers.TryGetValue(completed.ExecutorId, out var tracker))
        {
            stepInputTokens = (int)Math.Min(int.MaxValue, tracker.LastInputTokens);

            var currentOutput = tracker.TotalOutputTokens;
            var previousOutput = outputSnapshot.TryGetValue(completed.ExecutorId, out var snap) ? snap : 0L;
            var deltaOutput = Math.Max(0, currentOutput - previousOutput);
            stepOutputTokens = (int)Math.Min(int.MaxValue, deltaOutput);
            outputSnapshot[completed.ExecutorId] = currentOutput;
        }

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
            TokenInputCount = stepInputTokens,
            TokenOutputCount = stepOutputTokens,
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
            TokenInputCount: stepInputTokens,
            TokenOutputCount: stepOutputTokens,
            ErrorMessage: null,
            SummaryJson: resultText));

        // 阶段 6 Phase 1：返回 step 级 token 让 RunWorkflowAsync 累加到 task 级 TotalTokenCount
        return (stepInputTokens, stepOutputTokens);
    }

    /// <summary>
    /// P3-1 修复 2026-05-24：把 AgentResponseEvent 持久化为时间线步骤。
    /// 与 <see cref="PersistAndPublishStep"/> 类似，但数据源是 AgentResponseEvent 而非 ExecutorCompletedEvent。
    /// SDK Magentic 模式下，每个子智能体回复都触发 AgentResponseEvent，这是时间线的主要数据来源。
    /// </summary>
    private (int InputTokens, int OutputTokens) PersistAndPublishAgentResponseStep(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        string agentId,
        string summaryJson,
        int sequence,
        IReadOnlyDictionary<string, TokenTrackingChatClient> trackers,
        IDictionary<string, long> outputSnapshot)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stepId = Guid.NewGuid().ToString("N");

        // 从对应 agent 的 TokenTrackingChatClient 读取 token 数据
        var stepInputTokens = 0;
        var stepOutputTokens = 0;
        if (!string.IsNullOrEmpty(agentId)
            && trackers.TryGetValue(agentId, out var tracker))
        {
            stepInputTokens = (int)Math.Min(int.MaxValue, tracker.LastInputTokens);

            var currentOutput = tracker.TotalOutputTokens;
            var previousOutput = outputSnapshot.TryGetValue(agentId, out var snap) ? snap : 0L;
            var deltaOutput = Math.Max(0, currentOutput - previousOutput);
            stepOutputTokens = (int)Math.Min(int.MaxValue, deltaOutput);
            outputSnapshot[agentId] = currentOutput;
        }

        var stepEntity = new OrchestrationStepEntity
        {
            Id = stepId,
            TaskId = taskId,
            ParentStepId = null,
            Sequence = sequence,
            AgentId = agentId,
            AgentName = agentId,
            Action = "speak",
            Status = "completed",
            StartedAt = nowMs,
            CompletedAt = nowMs,
            DurationMs = 0,
            TokenInputCount = stepInputTokens,
            TokenOutputCount = stepOutputTokens,
            ErrorMessage = null,
            SummaryJson = summaryJson,
        };

        try
        {
            stepRepo.InsertStep(stepEntity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "写入 AgentResponse OrchestrationStep 失败：taskId={TaskId}, stepId={StepId}", taskId, stepId);
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
            AgentId: agentId,
            AgentName: agentId,
            Action: "speak",
            Status: "completed",
            StartedAt: nowMs,
            CompletedAt: nowMs,
            DurationMs: 0,
            TokenInputCount: stepInputTokens,
            TokenOutputCount: stepOutputTokens,
            ErrorMessage: null,
            SummaryJson: summaryJson));

        return (stepInputTokens, stepOutputTokens);
    }

    /// <summary>
    /// P3-1 修复 2026-05-24：从 ExecutorCompletedEvent 构建结构化 SummaryJson。
    /// ExecutorCompletedEvent.Data 可能是 AgentResponse / string / 其他对象。
    /// 尝试提取有意义的文本放入 { "content": "..." } 格式。
    /// </summary>
    private static string BuildExecutorCompletedSummaryJson(ExecutorCompletedEvent completed)
    {
        var obj = new JsonObject();

        if (completed.Data is AgentResponse agentResp)
        {
            if (!string.IsNullOrWhiteSpace(agentResp.Text))
                obj["content"] = agentResp.Text;
        }
        else if (completed.Data is string str && !string.IsNullOrWhiteSpace(str))
        {
            obj["content"] = str;
        }
        else if (completed.Data is not null)
        {
            var raw = completed.Data.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
                obj["content"] = raw;
        }

        if (obj.Count == 0)
        {
            obj["content"] = $"[{completed.ExecutorId ?? "Orchestrator"} 执行完成]";
        }

        return obj.ToJsonString();
    }

    /// <summary>
    /// P3-1 修复 2026-05-24：从 AgentResponseEvent 构建结构化 SummaryJson。
    /// 输出格式：<c>{ "content": "...", "thinking": "...", "tool_calls": [...] }</c>
    /// 供 <see cref="ViewModels.Workspace.WorkflowTimelineStepVm.ParseSummaryContent"/> 解析。
    /// </summary>
    private static string BuildAgentResponseSummaryJson(AgentResponseEvent agentResponse)
    {
        var response = agentResponse.Response;

        // 提取 tool calls（如果有）
        var toolCallsJson = string.Empty;
        if (response.Messages is { Count: > 0 })
        {
            var toolCalls = new JsonArray();
            foreach (var msg in response.Messages)
            {
                if (msg.Contents is null) continue;
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        var tcObj = new JsonObject
                        {
                            ["name"] = fc.Name ?? "unknown",
                        };
                        if (fc.Arguments is { Count: > 0 })
                        {
                            var argsObj = new JsonObject();
                            foreach (var kvp in fc.Arguments)
                            {
                                argsObj[kvp.Key] = kvp.Value?.ToString();
                            }
                            tcObj["arguments"] = argsObj.ToJsonString();
                        }
                        toolCalls.Add(tcObj);
                    }
                    else if (content is FunctionResultContent fr)
                    {
                        // 尝试在最后一个 tool call 中补上 result
                        if (toolCalls.Count > 0)
                        {
                            var lastTc = toolCalls[toolCalls.Count - 1]?.AsObject();
                            if (lastTc is not null)
                            {
                                lastTc["result"] = fr.Result?.ToString()?.Length > 500
                                    ? fr.Result?.ToString()?[..500] + "..."
                                    : fr.Result?.ToString();
                            }
                        }
                    }
                }
            }
            if (toolCalls.Count > 0)
            {
                toolCallsJson = toolCalls.ToJsonString();
            }
        }

        // 构建结构化 JSON
        var obj = new JsonObject();

        // content = 主体文本
        var text = response.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            obj["content"] = text;
        }

        // thinking = 思考过程（如果模型支持 extended thinking）
        // SDK AgentResponse 没有直接的 thinking 字段，但某些模型在 Text 前面嵌入 <think>...</think>
        // 暂不解析，留给未来扩展

        // tool_calls
        if (!string.IsNullOrEmpty(toolCallsJson))
        {
            obj["tool_calls"] = JsonNode.Parse(toolCallsJson);
        }

        // 如果完全为空（agent 只回了空字符串且没有 tool calls），返回占位
        if (obj.Count == 0)
        {
            obj["content"] = "[无输出内容]";
        }

        return obj.ToJsonString();
    }

    private void HandleTaskCompleted(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        List<string> participantIds,
        long startedMs,
        int stepCount,
        long totalTokens,
        string? finalReport,
        AIAgent referenceAgent)
    {
        var completedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var report = string.IsNullOrEmpty(finalReport)
            ? $"[{request.SubMode} 已完成，但未产出最终回复]"
            : finalReport;

        taskRepo.UpdateCompleted(
            taskId,
            status: WorkflowTaskStatus.Completed.ToDbValue(),
            finalReport: report,
            errorMessage: null,
            completedAt: completedAtMs,
            lastActiveTimestamp: completedAtMs,
            totalTokenCount: totalTokens);

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
            CompletedAt: completedAtMs,
            AllowMemoryIngest: ResolveAllowMemoryIngest(request.ManagerAgentId)));

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
                    logger.LogWarning(ex, "Workflow 任务 {TaskId} (SubMode={SubMode}) 标题兜底生成失败", taskId, request.SubMode);
                }
            }, CancellationToken.None);
        }

        // 阶段 6 Phase 1：Magentic 实际 token 反馈校准（移动平均权重 7:1，决策 6-1-B）
        // 用真实数据修正 EstimatedTokenMultiplier 默认值（NewTaskDialog 成本警告会读取此值估算）
        TryUpdateMagenticTokenMultiplier(request.SubMode, totalTokens, participantIds.Count);

        // 阶段 5B：任务正常完成 → 清理 Checkpoint BLOB（避免长期堆积）
        TryCleanupCheckpoints(taskId, "completed");
    }

    /// <summary>
    /// 阶段 6 Phase 4：查 Manager 智能体的 <see cref="AgentEntity.AllowWorkflowMemory"/> 配置位（决策 6-4-A/B/C）。
    /// 用于填充 <see cref="WorkflowTaskCompletedArgs.AllowMemoryIngest"/>，
    /// Memory 插件 <c>MemoryWorkflowEventHandler</c> 据此决定是否把 FinalReport 写入长期记忆。
    /// 决策 6-4-B：default true 向后兼容 — ManagerAgentId 为空 / Agent 不存在 / 查询异常都返回 true（与 4B 入库行为一致）。
    /// 决策 6-4-A 修订：事件正常发，仅用此字段携带 owner 配置位；不在 host 端"直接不发"避免破坏其他订阅者
    ///   （WorkflowTaskListVm / TaskDetailVm / WebSocketWorkflowFeedRelayService 都订阅同一事件）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #5。
    /// </summary>
    private bool ResolveAllowMemoryIngest(string? managerAgentId)
    {
        if (string.IsNullOrEmpty(managerAgentId)) return true;
        try
        {
            var manager = agentService.GetById(managerAgentId);
            return manager?.AllowWorkflowMemory ?? true;
        }
        catch (Exception ex)
        {
            // 查询异常 fallback 到 true（保持 4B 入库行为，避免静默丢数据）；仅 LogWarning 记录
            logger.LogWarning(ex,
                "阶段 6 Phase 4：查 Manager 智能体 {AgentId} 的 AllowWorkflowMemory 配置失败，fallback 到 true",
                managerAgentId);
            return true;
        }
    }

    /// <summary>
    /// 阶段 6 Phase 1：Magentic 任务完成时，用实际 token 数据校准 Workflow.Magentic.EstimatedTokenMultiplier。
    /// 移动平均权重 7:1：newMultiplier = (oldMultiplier * 7 + sampleMultiplier) / 8
    /// 优点：保守校准，单次任务异常值（如工具调用爆炸）不会污染默认值
    /// 触发条件：
    /// - SubMode 必须为 magentic（不规范化大小写问题，因为 request.SubMode 已是规范化值）
    /// - actualTokens &gt; 0（有真实数据）
    /// - participantCount &gt; 0（避免除零）
    /// - oldMultiplier 在合理区间 [100, 50000]（避免历史脏数据破坏）
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #2 / 5B Phase 4 §5.1。
    /// </summary>
    private void TryUpdateMagenticTokenMultiplier(string subMode, long actualTokens, int participantCount)
    {
        if (!string.Equals(subMode?.Trim().ToLowerInvariant(), "magentic", StringComparison.Ordinal))
            return;
        if (actualTokens <= 0 || participantCount <= 0)
            return;

        try
        {
            var maxRounds = options.MaxRounds;
            if (maxRounds <= 0) return;

            // 实际样本：actualTokens / (MaxRounds × participantCount) = 平均每参与者每轮 token
            var sampleMultiplier = actualTokens / ((long)maxRounds * participantCount);
            if (sampleMultiplier <= 0) return;

            var oldMultiplier = systemSettings.GetValue("Workflow.Magentic.EstimatedTokenMultiplier", 2000);
            // 合理区间防御（避免一次脏数据永久污染默认值）
            if (oldMultiplier < 100 || oldMultiplier > 50000)
            {
                logger.LogDebug(
                    "Magentic token multiplier 越界 ({Old})，跳过校准；本次样本 = {Sample}", oldMultiplier, sampleMultiplier);
                return;
            }

            var newMultiplier = (int)Math.Clamp((oldMultiplier * 7L + sampleMultiplier) / 8L, 100, 50000);
            systemSettings.SetValue("Workflow.Magentic.EstimatedTokenMultiplier", newMultiplier.ToString());

            logger.LogInformation(
                "Magentic token multiplier 校准：old={Old} → new={New}（sample={Sample}, actualTokens={Tokens}, rounds={Rounds}, participants={Participants}）",
                oldMultiplier, newMultiplier, sampleMultiplier, actualTokens, maxRounds, participantCount);
        }
        catch (Exception ex)
        {
            // 校准失败仅 warn，不影响主流程
            logger.LogWarning(ex, "Magentic token multiplier 校准失败（actualTokens={Tokens}, participants={Participants}）",
                actualTokens, participantCount);
        }
    }

    /// <summary>
    /// 阶段 5B：清理某任务的全部 Checkpoint BLOB。在 task completed/failed/cancelled 时调用。
    /// 失败仅 LogWarning，不抛出异常（不影响主流程）。
    /// </summary>
    private void TryCleanupCheckpoints(string taskId, string reason)
    {
        try
        {
            var deleted = checkpointRepo.DeleteByTaskId(taskId);
            if (deleted > 0)
            {
                logger.LogDebug(
                    "Workflow 任务 {TaskId} 已清理 {Count} 个 Checkpoint（原因={Reason}）",
                    taskId, deleted, reason);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workflow 任务 {TaskId} 清理 Checkpoint 失败（原因={Reason}），可能导致 BLOB 堆积",
                taskId, reason);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  HITL（人在回路）暂停 / 恢复（阶段 5B 新增）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 处理 SDK <see cref="RequestInfoEvent"/>：把 RequestInfo 转译为 paused 事件 + 阻塞等待用户响应 + 回写 SDK。
    /// 调用方为 <see cref="RunWorkflowAsync"/> 的 switch 分支。
    /// 用户响应通过 <see cref="ResumeAsync"/> 解锁 Tcs。
    /// </summary>
    private async Task HandleHitlRequestAsync(
        string taskId,
        WorkflowTaskRequest request,
        string traceId,
        OrchestrationTaskEntity entity,
        StreamingRun run,
        RequestInfoEvent requestInfo,
        CancellationToken ct)
    {
        var requestId = requestInfo.Request.RequestId;
        var tcs = new TaskCompletionSource<ExternalResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var ctx = new PendingHitlContext(taskId, requestId, requestInfo.Request, run, tcs, DateTimeOffset.UtcNow);
        _pausedTasks[taskId] = ctx;

        // 1) 序列化 RequestInfo payload（用于 UI 渲染计划详情）。
        //    Magentic RequirePlanSignoff(true) 触发的 payload 类型是 MagenticPlanReviewRequest；
        //    其他扩展类型走通用 fallback：序列化为简化 JSON 对象。
        //    注意：SDK 的 WorkflowsJsonUtilities 是 internal 不可访问，因此手动构造 JSON。
        string? requestPayloadJson = null;
        string pauseReason;
        if (requestInfo.Request.TryGetDataAs<MagenticPlanReviewRequest>(out var planReview))
        {
            pauseReason = "magentic.plan.signoff";
            try
            {
                // 手动构造 JSON：UI 只需 plan text + isStalled + 可选 progress 摘要
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("plan", planReview.Plan?.Text ?? string.Empty);
                    writer.WriteBoolean("isStalled", planReview.IsStalled);
                    if (planReview.CurrentProgress is { } progress)
                    {
                        writer.WriteBoolean("isStarted", progress.IsStarted);
                        writer.WriteBoolean("isRequestSatisfied", progress.IsRequestSatisfied);
                        writer.WriteBoolean("isInLoop", progress.IsInLoop);
                        writer.WriteBoolean("isProgressBeingMade", progress.IsProgressBeingMade);
                        writer.WriteString("nextSpeaker", progress.NextSpeaker ?? string.Empty);
                        writer.WriteString("instructionOrQuestion", progress.InstructionOrQuestion ?? string.Empty);
                    }
                    writer.WriteEndObject();
                }
                requestPayloadJson = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workflow 任务 {TaskId} 序列化 MagenticPlanReviewRequest 失败", taskId);
                requestPayloadJson = $"{{\"plan\":\"{planReview.Plan?.Text ?? "(empty)"}\",\"isStalled\":{(planReview.IsStalled ? "true" : "false")}}}";
            }
        }
        else
        {
            pauseReason = "external.request";
            requestPayloadJson = null;
        }

        // 2) 落库 Paused + 发 OnWorkflowTaskPaused 事件
        taskRepo.UpdatePaused(taskId, pausedAt);

        publisher.Publish(Events.OnWorkflowTaskPaused, new WorkflowTaskPausedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            PauseReason: pauseReason,
            RequestId: requestId,
            RequestPayloadJson: requestPayloadJson,
            PausedAt: pausedAt));

        logger.LogInformation(
            "Workflow 任务 {TaskId} 进入 HITL 暂停：reason={Reason}, requestId={RequestId}",
            taskId, pauseReason, requestId);

        // 3) 阻塞等待用户响应（外部 ResumeAsync 调用 Tcs.SetResult；超时由 RunWorkflowAsync 的 ct 兜底）
        ExternalResponse? response;
        try
        {
            response = await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _pausedTasks.TryRemove(taskId, out _);
        }

        if (response is null)
        {
            // 用户拒绝 / 超时 → 抛 OperationCanceledException 走 HandleTaskCancelled 路径
            throw new OperationCanceledException($"HITL 任务 {taskId} 被用户拒绝或超时");
        }

        // 4) 回写响应到 SDK，事件循环继续
        await run.SendResponseAsync(response);

        var resumedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        taskRepo.UpdateRunning(taskId, resumedAt);

        publisher.Publish(Events.OnWorkflowTaskResumed, new WorkflowTaskResumedArgs(
            TaskId: taskId,
            SourceSessionId: request.SourceSessionId,
            TraceId: traceId,
            WorkspaceId: request.WorkspaceId,
            Mode: request.Mode,
            SubMode: request.SubMode,
            OccurredAt: DateTimeOffset.UtcNow,
            Title: entity.Title,
            RequestId: requestId,
            ResumeAction: response.TryGetDataAs<MagenticPlanReviewResponse>(out var planResp) && planResp.Review.Count == 0
                ? "approved"
                : "revised",
            RevisionPayloadJson: null,
            ResumedAt: resumedAt));

        logger.LogInformation("Workflow 任务 {TaskId} 已恢复执行", taskId);
    }

    /// <summary>
    /// <see cref="IWorkflowExecutor.ResumeAsync"/> 实现：把用户响应解锁到事件循环。
    /// 详见 <see cref="HandleHitlRequestAsync"/>。
    /// </summary>
    public Task<bool> ResumeAsync(
        string taskId,
        string requestId,
        string action,
        IReadOnlyList<ChatMessage>? revisionMessages,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(requestId))
        {
            return Task.FromResult(false);
        }

        if (!_pausedTasks.TryGetValue(taskId, out var ctx))
        {
            logger.LogWarning("ResumeAsync：任务 {TaskId} 不在 paused 状态", taskId);
            return Task.FromResult(false);
        }

        if (!string.Equals(ctx.RequestId, requestId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "ResumeAsync：requestId 不配对（任务 {TaskId} 当前 {Current}，传入 {Incoming}），拒绝响应",
                taskId, ctx.RequestId, requestId);
            return Task.FromResult(false);
        }

        ExternalResponse? response = action?.ToLowerInvariant() switch
        {
            "approved" => ctx.Request.CreateResponse(new MagenticPlanReviewResponse([])),
            "revised" => ctx.Request.CreateResponse(new MagenticPlanReviewResponse(
                revisionMessages?.ToList() ?? [])),
            "rejected" => null,   // null 触发 OperationCanceledException 走 HandleTaskCancelled
            _ => null
        };

        var ok = ctx.Tcs.TrySetResult(response);
        if (!ok)
        {
            logger.LogWarning("ResumeAsync：TaskCompletionSource 已完成，重复响应被忽略 ({TaskId})", taskId);
        }
        return Task.FromResult(ok);
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

        // 阶段 5B：任务被取消 → 清理 Checkpoint BLOB
        TryCleanupCheckpoints(taskId, "cancelled");
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

        // 阶段 5B：任务异常失败 → 清理 Checkpoint BLOB
        TryCleanupCheckpoints(taskId, "failed");
    }
}
