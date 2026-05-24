using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine.Agents;
using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.AI.TaskEngine.Persistence;
using Netor.Cortana.AI.TaskEngine.Scheduling;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.TaskEngine;

/// <summary>
/// P4 任务执行引擎核心。
/// 驱动整个四阶段流程：需求分析 → 计划制定 → 执行 → 验证。
/// 不直接做任何 LLM 调用，所有工作委托给主智能体和子智能体。
///
/// 实现 <see cref="IHostedService"/>：
/// - StartAsync：扫描并恢复中断的任务（孤儿清理）
/// - StopAsync：取消所有运行中任务并等待其结束
///
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.1。
/// </summary>
public sealed class TaskExecutionEngine : IHostedService
{
    private readonly IOrchestratorAgent _orchestrator;
    private readonly IStepScheduler _scheduler;
    private readonly IPlanPersistence _persistence;
    private readonly IPublisher _publisher;
    private readonly TaskEngineOptions _options;
    private readonly ILogger<TaskExecutionEngine> _logger;

    /// <summary>运行中的任务上下文。</summary>
    private readonly ConcurrentDictionary<string, RunningTaskEngineContext> _runningTasks = new();

    public TaskExecutionEngine(
        IOrchestratorAgent orchestrator,
        IStepScheduler scheduler,
        IPlanPersistence persistence,
        IPublisher publisher,
        TaskEngineOptions options,
        ILogger<TaskExecutionEngine> logger)
    {
        _orchestrator = orchestrator;
        _scheduler = scheduler;
        _persistence = persistence;
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // IHostedService
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 应用启动时调用。扫描持久化目录中未完成的任务，尝试恢复或标记为失败。
    /// P4-7 收尾：实现断点恢复逻辑。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("P4 任务执行引擎已启动（MaxParallel={MaxParallel}, MaxLlm={MaxLlm}）",
            _options.MaxParallelSteps, _options.MaxLlmConcurrency);

        await RecoverInterruptedTasksAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 扫描所有任务的 checkpoint.json，找到中断的任务并标记为失败。
    /// 当前策略：中断的任务不自动恢复执行（避免用户未在场时意外消耗资源），
    /// 而是标记为 Paused 状态，等待用户手动恢复。
    /// </summary>
    private async Task RecoverInterruptedTasksAsync(CancellationToken ct)
    {
        try
        {
            var taskIds = _persistence.ListTaskIds();
            var recoveredCount = 0;

            foreach (var taskId in taskIds)
            {
                if (ct.IsCancellationRequested) break;

                var checkpoint = await _persistence.LoadCheckpointAsync(taskId, ct).ConfigureAwait(false);
                if (checkpoint is null) continue;

                // 加载计划检查是否在执行中被中断
                var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
                if (plan is null) continue;

                // 只恢复 Executing 状态的计划（Running/Retrying 步骤说明是被中断的）
                if (plan.Status is not PlanStatus.Executing) continue;

                // 将中断的执行步骤标记为 Pending（下次恢复时重跑）
                var hasInterruptedSteps = false;
                foreach (var step in plan.Steps)
                {
                    if (step.Status is PlanStepStatus.Running or PlanStepStatus.Retrying)
                    {
                        step.Status = PlanStepStatus.Pending;
                        step.RetryCount = 0; // 重置重试计数
                        hasInterruptedSteps = true;
                    }
                }

                if (!hasInterruptedSteps) continue;

                // 标记计划为暂停（等待用户手动恢复）
                plan.Status = PlanStatus.Paused;
                plan.LastModifiedAt = DateTimeOffset.UtcNow;
                await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

                recoveredCount++;
                _logger.LogInformation(
                    "P4 断点恢复：任务 {TaskId} 已标记为暂停（中断于 {Phase} 阶段，{SavedAt}）",
                    taskId, checkpoint.CurrentPhase, checkpoint.SavedAt);
            }

            if (recoveredCount > 0)
            {
                _logger.LogInformation("P4 断点恢复完成：共处理 {Count} 个中断任务", recoveredCount);
            }
        }
        catch (Exception ex)
        {
            // 恢复失败不应阻止引擎启动
            _logger.LogError(ex, "P4 断点恢复扫描失败（不影响引擎正常启动）");
        }
    }

    /// <summary>
    /// 应用关闭时调用。取消所有运行中任务并等待其结束。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("P4 任务执行引擎正在关闭，取消 {Count} 个运行中任务...", _runningTasks.Count);

        // 1. 优雅关闭：为所有运行中任务保存关闭前检查点
        foreach (var (taskId, _) in _runningTasks)
        {
            try
            {
                var plan = await _persistence.LoadPlanAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (plan is not null && plan.Status == PlanStatus.Executing)
                {
                    await _persistence.SaveCheckpointAsync(taskId, new ExecutionCheckpoint
                    {
                        TaskId = taskId,
                        PlanVersion = plan.Version,
                        CurrentPhase = "executing",
                        CurrentStepId = null,
                        SavedAt = DateTimeOffset.UtcNow,
                        StepStatuses = plan.Steps.ToDictionary(s => s.StepId, s => s.Status),
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "P4 关闭前保存检查点失败: {TaskId}", taskId);
            }
        }

        // 2. 解除所有等待信号（避免 TCS 阻塞导致关闭超时）
        foreach (var (_, ctx) in _runningTasks)
        {
            ctx.ResumeSignal.TrySetCanceled();
            ctx.PlanConfirmationSignal.TrySetCanceled();
        }

        // 3. 取消所有运行中任务
        foreach (var (_, ctx) in _runningTasks)
        {
            await ctx.Cts.CancelAsync().ConfigureAwait(false);
        }

        // 4. 等待所有任务结束（带超时）
        var allTasks = _runningTasks.Values.Select(ctx => ctx.ExecutionTask).ToArray();
        if (allTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("部分任务未能在 10 秒内优雅结束");
            }
            catch (OperationCanceledException)
            {
                // 宿主强制关闭
            }
        }

        // 5. 清理资源
        foreach (var (_, ctx) in _runningTasks)
        {
            ctx.Dispose();
        }
        _runningTasks.Clear();

        _logger.LogInformation("P4 任务执行引擎已关闭");
    }

    // ══════════════════════════════════════════════════════════════════════
    // 公开 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 启动新任务。同步创建上下文 + 发布事件，异步启动执行循环（fire-and-forget）。
    /// </summary>
    /// <param name="userInput">用户的任务描述文本。</param>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="templateId">可选的模板 ID（选用模板时传入）。</param>
    /// <param name="options">可选的启动选项（模型/模式配置）。</param>
    /// <param name="ct">调用方取消令牌（仅用于启动阶段校验，不影响任务运行）。</param>
    /// <returns>新任务 ID。</returns>
    public async Task<string> StartTaskAsync(
        string userInput, string workspaceId, string? templateId,
        TaskStartOptions? options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        // workspaceId 允许为空（默认工作区场景）
        workspaceId ??= string.Empty;

        var taskId = Guid.NewGuid().ToString("N");

        // P4-2: 创建执行运行（生成 Run ID + 创建目录结构 + 写入 task.json / run.json / latest-run）
        var taskTitle = userInput.Length > 50 ? userInput[..50] + "…" : userInput;
        await _persistence.CreateRunAsync(taskId, taskTitle, ct).ConfigureAwait(false);

        // 将用户选择的模型/模式配置写入 TaskMeta
        if (options is not null)
        {
            var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
            if (meta is not null)
            {
                meta.ProviderId = options.ProviderId;
                meta.ModelId = options.ModelId;
                meta.SubMode = options.SubMode;
                await _persistence.SaveTaskMetaAsync(meta, ct).ConfigureAwait(false);
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 应用任务级超时
        cts.CancelAfter(_options.TaskTimeout);

        var executionTask = ExecuteFullLifecycleAsync(taskId, userInput, workspaceId, templateId, cts.Token);
        var context = new RunningTaskEngineContext(taskId, executionTask, cts, DateTimeOffset.UtcNow);

        if (!_runningTasks.TryAdd(taskId, context))
        {
            context.Dispose();
            throw new InvalidOperationException($"任务 ID 冲突: {taskId}");
        }

        _logger.LogInformation("P4 任务已启动: {TaskId} (RunId={RunId})", taskId, _persistence.GetActiveRunId(taskId));
        return taskId;
    }

    /// <summary>
    /// 启动新任务（无选项重载，向后兼容）。
    /// </summary>
    public Task<string> StartTaskAsync(string userInput, string workspaceId, string? templateId, CancellationToken ct)
        => StartTaskAsync(userInput, workspaceId, templateId, options: null, ct);

    /// <summary>
    /// 取消运行中的任务。
    /// P4-7 边界修复：如果任务正在暂停等待中（await ResumeSignal），
    /// 先取消 TCS 以解除阻塞，再取消 CTS 传播取消信号。
    /// </summary>
    /// <returns>true 表示已发取消信号；false 表示任务不存在或已结束。</returns>
    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct)
    {
        if (!_runningTasks.TryGetValue(taskId, out var context))
            return false;

        // P4-7: 解除暂停等待（TCS 可能正在 await 中）
        context.ResumeSignal.TrySetCanceled();

        await context.Cts.CancelAsync().ConfigureAwait(false);
        _logger.LogInformation("P4 任务已请求取消: {TaskId}", taskId);
        return true;
    }

    /// <summary>
    /// 暂停运行中的任务（用户主动暂停，软暂停语义）。
    /// 不中断当前正在执行的步骤，而是在步骤间检测暂停标志。
    /// 当前步骤执行完毕后，引擎进入暂停状态并等待恢复信号。
    /// </summary>
    /// <returns>true 表示已设置暂停请求；false 表示任务不存在或已在暂停中。</returns>
    public Task<bool> PauseTaskAsync(string taskId, CancellationToken ct)
    {
        if (!_runningTasks.TryGetValue(taskId, out var context))
            return Task.FromResult(false);

        if (context.PauseRequested)
            return Task.FromResult(false); // 已在暂停/暂停请求中

        context.PauseRequested = true;
        _logger.LogInformation("P4 任务暂停请求已设置: {TaskId}（将在当前步骤完成后暂停）", taskId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 恢复已暂停的任务。
    /// 如果暂停期间用户修改了计划（Version 增加），会自动进行增量 diff 分析，
    /// 将需要重做的步骤重置为 Pending，保留未受影响的已完成步骤。
    /// </summary>
    /// <returns>true 表示已恢复；false 表示任务不存在或不在暂停状态。</returns>
    public async Task<bool> ResumeTaskAsync(string taskId, CancellationToken ct)
    {
        if (!_runningTasks.TryGetValue(taskId, out var context))
            return false;

        // 仅在暂停状态下可恢复
        if (!context.PauseRequested)
            return false;

        // 加载当前计划（可能在暂停期间被用户修改过）
        var currentPlan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
        if (currentPlan is null)
        {
            _logger.LogWarning("P4 恢复失败：无法加载计划: {TaskId}", taskId);
            return false;
        }

        // 如果暂停期间计划被修改（Version 增加），进行增量 diff 分析
        if (context.PausedPlanSnapshot is not null &&
            currentPlan.Version > context.PausedPlanSnapshot.Version)
        {
            _logger.LogInformation(
                "P4 计划已修改 (v{OldVersion} → v{NewVersion})，执行增量分析: {TaskId}",
                context.PausedPlanSnapshot.Version, currentPlan.Version, taskId);

            var diff = await _orchestrator.AnalyzePlanDiffAsync(
                taskId, context.PausedPlanSnapshot, currentPlan, ct).ConfigureAwait(false);

            ApplyDiffResult(currentPlan, diff);
            await _persistence.SavePlanAsync(currentPlan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPlanUpdated,
                new TaskPlanEventArgs(taskId, DateTimeOffset.UtcNow,
                    currentPlan.PlanId, currentPlan.Version, currentPlan.Steps.Count));
        }

        // 如果有 WaitingUser 步骤被恢复，将其重置为 Pending
        foreach (var step in currentPlan.Steps.Where(s => s.Status == PlanStepStatus.WaitingUser))
        {
            step.Status = PlanStepStatus.Pending;
        }

        // 清除暂停状态并发送恢复信号
        context.PauseRequested = false;
        context.PausedPlanSnapshot = null;
        context.ResumeSignal.TrySetResult(true);

        _logger.LogInformation("P4 任务已恢复: {TaskId}", taskId);
        return true;
    }

    /// <summary>获取任务当前状态。</summary>
    public bool IsTaskRunning(string taskId) => _runningTasks.ContainsKey(taskId);

    /// <summary>获取所有运行中任务的 ID 列表。</summary>
    public IReadOnlyList<string> GetRunningTaskIds() => [.. _runningTasks.Keys];

    // ══════════════════════════════════════════════════════════════════════
    // P4 断点恢复 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 恢复中断的任务（应用重启后，之前被 RecoverInterruptedTasksAsync 标记为 Paused 的任务）。
    /// 从持久化的计划和检查点恢复执行，跳过已完成的步骤，从 Pending 步骤继续。
    /// </summary>
    /// <param name="taskId">要恢复的任务 ID（必须是 Paused 状态且不在 _runningTasks 中）。</param>
    /// <param name="ct">调用方取消令牌（仅用于启动阶段，不影响任务运行）。</param>
    /// <returns>true 表示恢复成功；false 表示任务不存在、无计划或不在 Paused 状态。</returns>
    public async Task<bool> ResumeInterruptedTaskAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        // 已在运行中的任务不能通过此接口恢复（应使用 ResumeTaskAsync）
        if (_runningTasks.ContainsKey(taskId))
        {
            _logger.LogWarning("P4 断点恢复失败：任务 {TaskId} 已在运行中（请使用 ResumeTaskAsync）", taskId);
            return false;
        }

        // 加载计划
        var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
        if (plan is null)
        {
            _logger.LogWarning("P4 断点恢复失败：任务 {TaskId} 无计划", taskId);
            return false;
        }

        // 仅恢复 Paused 状态的任务
        if (plan.Status is not PlanStatus.Paused)
        {
            _logger.LogWarning("P4 断点恢复失败：任务 {TaskId} 状态为 {Status}（期望 Paused）", taskId, plan.Status);
            return false;
        }

        // 创建新的执行上下文（重新进入执行循环）
        var cts = new CancellationTokenSource();
        cts.CancelAfter(_options.TaskTimeout);

        var executionTask = ResumeExecutionFromCheckpointAsync(taskId, plan, cts.Token);
        var context = new RunningTaskEngineContext(taskId, executionTask, cts, DateTimeOffset.UtcNow);

        if (!_runningTasks.TryAdd(taskId, context))
        {
            context.Dispose();
            _logger.LogWarning("P4 断点恢复失败：任务 {TaskId} 上下文添加冲突", taskId);
            return false;
        }

        _logger.LogInformation("P4 断点恢复成功：任务 {TaskId} 已从检查点恢复执行", taskId);
        return true;
    }

    /// <summary>
    /// 从检查点恢复执行：跳过已完成步骤，从 Pending 步骤继续执行 → 验证 → 完成。
    /// </summary>
    private async Task ResumeExecutionFromCheckpointAsync(string taskId, ExecutionPlan plan, CancellationToken ct)
    {
        try
        {
            // 发布恢复事件
            _publisher.Publish(Events.OnTaskEngineResumed,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, "checkpoint_recovery"));

            // 获取运行时上下文
            if (!_runningTasks.TryGetValue(taskId, out var runContext))
                throw new InvalidOperationException($"运行时上下文丢失: {taskId}");

            // ── 继续执行阶段 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "executing"));

            await RunExecutionPhaseAsync(taskId, plan, runContext, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "executing"));

            // ── 验证阶段 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "validating"));

            var validationResult = await _orchestrator.RunValidationPhaseAsync(taskId, plan, ct)
                .ConfigureAwait(false);

            // 持久化验证结果 + 发布事件
            await _persistence.SaveValidationResultAsync(taskId, validationResult, ct)
                .ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskValidationCompleted,
                new TaskValidationEventArgs(taskId, DateTimeOffset.UtcNow,
                    validationResult.Passed, validationResult.Score,
                    validationResult.Summary, validationResult.Issues));

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "validating"));

            // ── 完成 ──
            plan.Status = PlanStatus.Completed;
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskEngineCompleted,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow,
                    validationResult.Summary));

            _logger.LogInformation("P4 断点恢复任务已完成: {TaskId}", taskId);
        }
        catch (OperationCanceledException)
        {
            _publisher.Publish(Events.OnTaskEngineFailed,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, "cancelled"));
            _logger.LogInformation("P4 断点恢复任务已取消: {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _publisher.Publish(Events.OnTaskEngineFailed,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, ex.Message));
            _logger.LogError(ex, "P4 断点恢复任务执行失败: {TaskId}", taskId);
        }
        finally
        {
            if (_runningTasks.TryRemove(taskId, out var ctx))
                ctx.Dispose();
        }
    }

    /// <summary>
    /// 获取所有可恢复的中断任务 ID（Paused 状态且不在运行中）。
    /// UI 可用此方法展示"恢复执行"按钮列表。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRecoverableTaskIdsAsync(CancellationToken ct)
    {
        var taskIds = _persistence.ListTaskIds();
        var recoverable = new List<string>();

        foreach (var taskId in taskIds)
        {
            if (_runningTasks.ContainsKey(taskId)) continue;

            var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
            if (plan?.Status == PlanStatus.Paused)
                recoverable.Add(taskId);
        }

        return recoverable;
    }

    // ══════════════════════════════════════════════════════════════════════
    // P4 任务元数据管理 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取任务状态信息。
    /// 优先从运行时上下文获取（运行中任务），回退到持久化的 TaskMeta。
    /// </summary>
    public async Task<TaskStatusInfo?> GetTaskStatusAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        // 运行中任务：从内存上下文获取实时状态
        if (_runningTasks.TryGetValue(taskId, out var ctx))
        {
            var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
            return new TaskStatusInfo
            {
                TaskId = taskId,
                Status = ctx.PauseRequested ? "paused" : "running",
                PlanStatus = plan?.Status,
                StartedAt = ctx.StartedAt,
                CompletedSteps = plan?.Steps.Count(s => s.Status == PlanStepStatus.Completed) ?? 0,
                TotalSteps = plan?.Steps.Count ?? 0,
            };
        }

        // 已结束任务：从持久化 TaskMeta 获取
        var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
        if (meta is null) return null;

        return new TaskStatusInfo
        {
            TaskId = taskId,
            Status = meta.Status,
            StartedAt = meta.CreatedAt,
            Title = meta.Title,
        };
    }

    /// <summary>
    /// 列出所有任务（按创建时间倒序）。
    /// </summary>
    public async Task<IReadOnlyList<TaskStatusInfo>> ListTasksAsync(CancellationToken ct)
    {
        var metas = await _persistence.ListTaskMetasAsync(ct).ConfigureAwait(false);
        var result = new List<TaskStatusInfo>(metas.Count);

        foreach (var meta in metas)
        {
            // 如果任务正在运行中，用运行时状态覆盖
            if (_runningTasks.TryGetValue(meta.TaskId, out var ctx))
            {
                result.Add(new TaskStatusInfo
                {
                    TaskId = meta.TaskId,
                    Status = ctx.PauseRequested ? "paused" : "running",
                    Title = meta.Title,
                    StartedAt = ctx.StartedAt,
                });
            }
            else
            {
                result.Add(new TaskStatusInfo
                {
                    TaskId = meta.TaskId,
                    Status = meta.Status,
                    Title = meta.Title,
                    StartedAt = meta.CreatedAt,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 获取任务详情（包含执行计划）。
    /// </summary>
    public async Task<TaskDetailInfo?> GetTaskDetailAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
        if (meta is null) return null;

        var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
        var requirements = await _persistence.LoadRequirementsAsync(taskId, ct).ConfigureAwait(false);

        return new TaskDetailInfo
        {
            TaskId = taskId,
            Title = meta.Title,
            Status = _runningTasks.TryGetValue(taskId, out var ctx)
                ? (ctx.PauseRequested ? "paused" : "running")
                : meta.Status,
            CreatedAt = meta.CreatedAt,
            Plan = plan,
            Requirements = requirements,
        };
    }

    /// <summary>
    /// 删除已完成/失败的任务。运行中的任务不可删除（需先取消）。
    /// </summary>
    /// <returns>true 表示删除成功；false 表示任务不存在或正在运行中。</returns>
    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        // 运行中的任务不可直接删除
        if (_runningTasks.ContainsKey(taskId))
        {
            _logger.LogWarning("P4 删除失败：任务 {TaskId} 正在运行中，需先取消", taskId);
            return false;
        }

        await _persistence.DeleteTaskAsync(taskId, ct).ConfigureAwait(false);
        _logger.LogInformation("P4 任务已删除: {TaskId}", taskId);
        return true;
    }

    /// <summary>
    /// 重命名任务标题。
    /// </summary>
    /// <returns>true 表示重命名成功；false 表示任务不存在。</returns>
    public async Task<bool> RenameTaskAsync(string taskId, string newTitle, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTitle);

        var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
        if (meta is null) return false;

        meta.Title = newTitle.Trim();
        meta.LastModifiedAt = DateTimeOffset.UtcNow;
        await _persistence.SaveTaskMetaAsync(meta, ct).ConfigureAwait(false);

        _logger.LogInformation("P4 任务已重命名: {TaskId} → {Title}", taskId, meta.Title);
        return true;
    }

    /// <summary>
    /// 切换任务置顶状态。
    /// </summary>
    /// <returns>true 表示操作成功；false 表示任务不存在。</returns>
    public async Task<bool> SetPinnedAsync(string taskId, bool isPinned, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
        if (meta is null) return false;

        meta.IsPinned = isPinned;
        meta.LastModifiedAt = DateTimeOffset.UtcNow;
        await _persistence.SaveTaskMetaAsync(meta, ct).ConfigureAwait(false);

        _logger.LogDebug("P4 任务置顶状态已更新: {TaskId} → {IsPinned}", taskId, isPinned);
        return true;
    }

    /// <summary>
    /// 设置任务归档状态。
    /// </summary>
    /// <returns>true 表示操作成功；false 表示任务不存在。</returns>
    public async Task<bool> SetArchivedAsync(string taskId, bool isArchived, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var meta = await _persistence.LoadTaskMetaAsync(taskId, ct).ConfigureAwait(false);
        if (meta is null) return false;

        meta.IsArchived = isArchived;
        meta.LastModifiedAt = DateTimeOffset.UtcNow;
        await _persistence.SaveTaskMetaAsync(meta, ct).ConfigureAwait(false);

        _logger.LogDebug("P4 任务归档状态已更新: {TaskId} → {IsArchived}", taskId, isArchived);
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // P4-6: 模板管理 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 将已完成任务的执行计划保存为模板。
    /// 从计划中提取步骤结构（标题/描述/执行模式/依赖/智能体类型），
    /// 去掉运行时状态（结果/时间戳/重试计数），生成可复用的模板。
    /// </summary>
    /// <param name="taskId">来源任务 ID（必须有已完成的计划）。</param>
    /// <param name="templateName">模板名称（用户命名）。</param>
    /// <param name="description">模板描述（可选）。</param>
    /// <param name="tags">适用场景标签（可选）。</param>
    /// <param name="workspaceId">所属工作区 ID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>保存的模板 ID；如果任务无计划则返回 null。</returns>
    public async Task<string?> SaveAsTemplateAsync(
        string taskId, string templateName, string? description,
        IReadOnlyList<string>? tags, string workspaceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);

        var plan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
        if (plan is null)
        {
            _logger.LogWarning("P4-6 保存模板失败：任务 {TaskId} 无计划", taskId);
            return null;
        }

        var templateId = Guid.NewGuid().ToString("N");
        var template = new ExecutionTemplate
        {
            TemplateId = templateId,
            Name = templateName,
            Description = description ?? string.Empty,
            Tags = tags?.ToList() ?? [],
            SourceTaskId = taskId,
            WorkspaceId = workspaceId,
            CreatedAt = DateTimeOffset.UtcNow,
            UsageCount = 0,
            Steps = plan.Steps.Select(s => new TemplateStep
            {
                Sequence = s.Sequence,
                Title = s.Title,
                Description = s.Description,
                ExecutionMode = s.ExecutionMode,
                DependsOn = [.. s.DependsOn],
                AgentTypeDescription = s.AgentTypeDescription,
                RequiredTools = s.RequiredTools?.ToList() ?? [],
                RequireUserConfirmation = s.ExecutionMode == "await_user",
                SubTasks = s.SubTasks?.Select(st => new TemplateSubTask
                {
                    Title = st.Title,
                    Description = st.Description,
                    AgentTypeDescription = st.AgentTypeDescription,
                }).ToList() ?? [],
            }).ToList(),
        };

        await _persistence.SaveTemplateAsync(template, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "P4-6 模板已保存: {TemplateId} ({TemplateName}) from task {TaskId}, {StepCount} 步",
            templateId, templateName, taskId, template.Steps.Count);

        return templateId;
    }

    /// <summary>列出指定工作区的所有执行模板。</summary>
    public Task<IReadOnlyList<ExecutionTemplate>> ListTemplatesAsync(string workspaceId, CancellationToken ct)
        => _persistence.ListTemplatesAsync(workspaceId, ct);

    /// <summary>加载指定模板。</summary>
    public Task<ExecutionTemplate?> LoadTemplateAsync(string templateId, CancellationToken ct)
        => _persistence.LoadTemplateAsync(templateId, ct);

    // ══════════════════════════════════════════════════════════════════════
    // P4 用户确认计划 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 用户确认当前计划，引擎进入执行阶段。
    /// UI 在收到 <see cref="Events.OnTaskPlanCreated"/> 事件后展示计划，
    /// 用户点击"确认执行"按钮时调用此方法。
    /// </summary>
    /// <returns>true 表示确认成功；false 表示任务不存在或不在等待确认状态。</returns>
    public Task<bool> ConfirmPlanAsync(string taskId, CancellationToken ct)
    {
        if (!_runningTasks.TryGetValue(taskId, out var context))
            return Task.FromResult(false);

        if (!context.WaitingPlanConfirmation)
            return Task.FromResult(false);

        context.PlanConfirmationSignal.TrySetResult(null); // null = 确认
        _logger.LogInformation("P4 用户已确认计划: {TaskId}", taskId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 用户请求修改计划。引擎将用户的修改意见传给策划子智能体重新生成计划。
    /// UI 在计划确认面板中用户输入修改意见并点击"修改"按钮时调用。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="modificationRequest">用户的修改意见（自然语言描述）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 表示修改请求已接受；false 表示任务不存在或不在等待确认状态。</returns>
    public Task<bool> RequestPlanModificationAsync(string taskId, string modificationRequest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modificationRequest);

        if (!_runningTasks.TryGetValue(taskId, out var context))
            return Task.FromResult(false);

        if (!context.WaitingPlanConfirmation)
            return Task.FromResult(false);

        context.PlanConfirmationSignal.TrySetResult(modificationRequest); // non-null = 修改意见
        _logger.LogInformation("P4 用户请求修改计划: {TaskId} ({Request})",
            taskId, modificationRequest.Length > 50 ? modificationRequest[..50] + "…" : modificationRequest);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 查询指定任务是否正在等待用户确认计划。
    /// UI 可用此方法判断是否显示确认按钮。
    /// </summary>
    public bool IsWaitingPlanConfirmation(string taskId)
    {
        return _runningTasks.TryGetValue(taskId, out var context) && context.WaitingPlanConfirmation;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部执行循环
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 完整任务生命周期：需求分析 → 计划制定 → 执行 → 验证 → 完成。
    /// 从 StartTaskAsync 以 fire-and-forget 方式启动。
    /// </summary>
    private async Task ExecuteFullLifecycleAsync(
        string taskId, string userInput, string workspaceId, string? templateId, CancellationToken ct)
    {
        try
        {
            // ── 阶段 1: 需求分析 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "requirements"));

            var requirements = await _orchestrator.RunRequirementsPhaseAsync(taskId, userInput, ct)
                .ConfigureAwait(false);
            await _persistence.SaveRequirementsAsync(taskId, requirements, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "requirements"));

            // ── 阶段 2: 计划制定 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "planning"));

            ExecutionTemplate? template = null;
            if (!string.IsNullOrEmpty(templateId))
                template = await _persistence.LoadTemplateAsync(templateId, ct).ConfigureAwait(false);

            var plan = await _orchestrator.RunPlanningPhaseAsync(taskId, requirements, template, ct)
                .ConfigureAwait(false);
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPlanCreated,
                new TaskPlanEventArgs(taskId, DateTimeOffset.UtcNow, plan.PlanId, plan.Version, plan.Steps.Count));

            // 等待用户确认计划（支持多轮修改）
            plan = await WaitForPlanConfirmationAsync(taskId, plan, requirements, template, ct)
                .ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPlanConfirmed,
                new TaskPlanEventArgs(taskId, DateTimeOffset.UtcNow, plan.PlanId, plan.Version, plan.Steps.Count));
            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "planning"));

            // ── 阶段 3: 执行 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "executing"));

            // P4-5: 获取运行时上下文以支持暂停/恢复
            if (!_runningTasks.TryGetValue(taskId, out var runContext))
                throw new InvalidOperationException($"运行时上下文丢失: {taskId}");

            await RunExecutionPhaseAsync(taskId, plan, runContext, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "executing"));

            // ── 阶段 4: 验证 ──
            _publisher.Publish(Events.OnTaskPhaseStarted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "validating"));

            var validationResult = await _orchestrator.RunValidationPhaseAsync(taskId, plan, ct)
                .ConfigureAwait(false);

            // 持久化验证结果 + 发布事件
            await _persistence.SaveValidationResultAsync(taskId, validationResult, ct)
                .ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskValidationCompleted,
                new TaskValidationEventArgs(taskId, DateTimeOffset.UtcNow,
                    validationResult.Passed, validationResult.Score,
                    validationResult.Summary, validationResult.Issues));

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "validating"));

            // ── 完成 ──
            plan.Status = PlanStatus.Completed;
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskEngineCompleted,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow,
                    validationResult.Summary));

            _logger.LogInformation("P4 任务已完成: {TaskId}", taskId);
        }
        catch (OperationCanceledException)
        {
            _publisher.Publish(Events.OnTaskEngineFailed,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, "cancelled"));
            _logger.LogInformation("P4 任务已取消: {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _publisher.Publish(Events.OnTaskEngineFailed,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, ex.Message));
            _logger.LogError(ex, "P4 任务执行失败: {TaskId}", taskId);
        }
        finally
        {
            // 清理运行时上下文
            if (_runningTasks.TryRemove(taskId, out var ctx))
            {
                ctx.Dispose();
            }
        }
    }

    /// <summary>
    /// 等待用户确认计划。支持多轮修改：用户可反复请求修改，每次由策划子智能体重新生成。
    /// 如果配置了 <see cref="TaskEngineOptions.AutoConfirmPlan"/>，则跳过等待直接确认。
    /// </summary>
    /// <returns>用户最终确认的计划（可能经过多轮修改，Version 递增）。</returns>
    private async Task<ExecutionPlan> WaitForPlanConfirmationAsync(
        string taskId,
        ExecutionPlan plan,
        RequirementsAnalysis requirements,
        ExecutionTemplate? template,
        CancellationToken ct)
    {
        // 自动确认模式：跳过用户交互
        if (_options.AutoConfirmPlan)
        {
            _logger.LogInformation("P4 自动确认计划: {TaskId}（AutoConfirmPlan=true）", taskId);
            plan.Status = PlanStatus.Confirmed;
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);
            return plan;
        }

        if (!_runningTasks.TryGetValue(taskId, out var context))
            throw new InvalidOperationException($"运行时上下文丢失: {taskId}");

        const int maxModificationRounds = 5; // 防止无限修改循环

        for (var round = 0; round < maxModificationRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            // 标记等待状态（UI 据此显示确认/修改按钮）
            context.WaitingPlanConfirmation = true;
            context.ResetPlanConfirmationSignal();

            _logger.LogInformation("P4 等待用户确认计划: {TaskId} (v{Version}, 第{Round}轮)",
                taskId, plan.Version, round + 1);

            // 阻塞等待用户响应
            string? userResponse;
            try
            {
                userResponse = await context.PlanConfirmationSignal.Task
                    .WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                context.WaitingPlanConfirmation = false;
            }

            // null = 用户确认，进入执行
            if (userResponse is null)
            {
                plan.Status = PlanStatus.Confirmed;
                await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);
                _logger.LogInformation("P4 计划已确认: {TaskId} (v{Version})", taskId, plan.Version);
                return plan;
            }

            // non-null = 用户修改意见，重新生成计划
            _logger.LogInformation(
                "P4 用户请求修改计划: {TaskId} (v{Version})，修改意见: {Feedback}",
                taskId, plan.Version,
                userResponse.Length > 80 ? userResponse[..80] + "…" : userResponse);

            // 将修改意见追加到需求约束中，传给策划子智能体重新规划
            var modifiedRequirements = new RequirementsAnalysis
            {
                OriginalInput = requirements.OriginalInput,
                KeyPoints = [.. requirements.KeyPoints],
                Constraints = [.. requirements.Constraints, $"用户修改意见 (v{plan.Version}): {userResponse}"],
                ExpectedDeliverable = requirements.ExpectedDeliverable,
                ComplexityLevel = requirements.ComplexityLevel,
            };

            plan = await _orchestrator.RunPlanningPhaseAsync(taskId, modifiedRequirements, template, ct)
                .ConfigureAwait(false);
            plan.Version = round + 2; // 版本号递增
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

            // 发布计划更新事件（UI 刷新步骤列表）
            _publisher.Publish(Events.OnTaskPlanUpdated,
                new TaskPlanEventArgs(taskId, DateTimeOffset.UtcNow,
                    plan.PlanId, plan.Version, plan.Steps.Count));
        }

        // 超过最大修改轮次，强制确认
        _logger.LogWarning("P4 计划修改超过 {Max} 轮，强制确认: {TaskId}", maxModificationRounds, taskId);
        plan.Status = PlanStatus.Confirmed;
        await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// 执行阶段主循环：调度器驱动，按依赖顺序执行步骤，支持并行。
    /// P4-5：增加暂停检测、失败自动暂停、用户确认等待。
    /// </summary>
    private async Task RunExecutionPhaseAsync(
        string taskId, ExecutionPlan plan, RunningTaskEngineContext context, CancellationToken ct)
    {
        plan.Status = PlanStatus.Executing;
        await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

        while (!_scheduler.IsAllCompleted(plan) && !ct.IsCancellationRequested)
        {
            // ── P4-5: 检查暂停请求（用户主动暂停 / 自动暂停） ──
            if (context.PauseRequested)
            {
                await HandlePauseAsync(taskId, plan, context, ct).ConfigureAwait(false);

                // P4-7 边界修复：恢复后重新加载计划（暂停期间可能被用户修改过）
                var reloadedPlan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
                if (reloadedPlan is not null)
                {
                    plan = reloadedPlan;
                }
                else
                {
                    _logger.LogWarning("P4 恢复后无法重新加载计划，继续使用内存中的计划: {TaskId}", taskId);
                }
                continue; // 回到循环顶部重新评估
            }

            // 1. 检查是否有失败步骤 → 自动暂停等待用户决策
            if (_scheduler.HasFailedSteps(plan))
            {
                context.PauseRequested = true;
                continue; // 下一轮循环进入 HandlePauseAsync
            }

            // 2. 检查是否有步骤在等待用户确认 → 自动暂停
            if (_scheduler.IsWaitingUser(plan))
            {
                context.PauseRequested = true;
                continue; // 下一轮循环进入 HandlePauseAsync
            }

            // 3. 获取当前可执行的步骤批次
            var readySteps = _scheduler.GetReadySteps(plan);

            if (readySteps.Count == 0)
            {
                // 无可执行步骤但也没全部完成 → 可能存在循环依赖或调度死锁
                _logger.LogError("P4 调度死锁：任务 {TaskId} 无可执行步骤但未全部完成", taskId);
                throw new InvalidOperationException("调度死锁：无可执行步骤但计划未完成（检查步骤依赖是否有循环）");
            }

            // 4. 并行执行（受 MaxParallelSteps 限制）
            var batch = readySteps.Take(_options.MaxParallelSteps).ToList();
            var stepTasks = batch.Select(step =>
                ExecuteStepWithRetryAsync(taskId, plan, step, ct));
            await Task.WhenAll(stepTasks).ConfigureAwait(false);

            // 5. 持久化进度 + 断点
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);
            await _persistence.SaveCheckpointAsync(taskId, new ExecutionCheckpoint
            {
                TaskId = taskId,
                PlanVersion = plan.Version,
                CurrentPhase = "executing",
                CurrentStepId = null,
                SavedAt = DateTimeOffset.UtcNow,
                StepStatuses = plan.Steps.ToDictionary(s => s.StepId, s => s.Status),
            }, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// P4-5：处理暂停状态 — 保存断点、发布暂停事件、等待恢复信号。
    /// 由执行循环在检测到 PauseRequested 时调用。
    /// </summary>
    private async Task HandlePauseAsync(
        string taskId, ExecutionPlan plan,
        RunningTaskEngineContext context, CancellationToken ct)
    {
        // 1. 确定暂停原因
        var reason = _scheduler.HasFailedSteps(plan) ? "step_failed"
            : _scheduler.IsWaitingUser(plan) ? "waiting_user_confirmation"
            : "user_requested";

        // 2. 保存暂停状态
        plan.Status = PlanStatus.Paused;
        await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);
        await _persistence.SaveCheckpointAsync(taskId, new ExecutionCheckpoint
        {
            TaskId = taskId,
            PlanVersion = plan.Version,
            CurrentPhase = "executing",
            CurrentStepId = null,
            SavedAt = DateTimeOffset.UtcNow,
            StepStatuses = plan.Steps.ToDictionary(s => s.StepId, s => s.Status),
        }, ct).ConfigureAwait(false);

        // 3. 快照计划（用于恢复时 diff 比较）
        context.PausedPlanSnapshot = ClonePlanForDiff(plan);

        // 4. 发布暂停事件
        _publisher.Publish(Events.OnTaskEnginePaused,
            new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow, reason));

        _logger.LogInformation("P4 任务已暂停: {TaskId} (reason={Reason})", taskId, reason);

        // 5. 等待恢复信号（阻塞直到 ResumeTaskAsync 被调用）
        context.ResetResumeSignal();
        await context.ResumeSignal.Task.WaitAsync(ct).ConfigureAwait(false);

        // 6. 恢复：更新状态并发布事件
        plan.Status = PlanStatus.Executing;
        _publisher.Publish(Events.OnTaskEngineResumed,
            new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow));

        _logger.LogInformation("P4 任务已恢复: {TaskId}", taskId);
    }

    /// <summary>
    /// P4-5：将增量 diff 分析结果应用到计划上。
    /// 需要重做的步骤重置为 Pending，保留的步骤不变。
    /// </summary>
    private static void ApplyDiffResult(ExecutionPlan plan, PlanDiffResult diff)
    {
        foreach (var step in plan.Steps)
        {
            if (diff.StepsToRedo.Contains(step.StepId))
            {
                // 重置为待执行（丢弃旧结果，准备重新执行）
                step.Status = PlanStepStatus.Pending;
                step.ResultSummary = null;
                step.ResultDetail = null;
                step.ErrorMessage = null;
                step.RetryCount = 0;
                step.ProgressPercent = 0;
                step.StartedAt = null;
                step.CompletedAt = null;
            }
            // StepsToKeep：不做处理（已完成的步骤保持 Completed）
        }
    }

    /// <summary>
    /// P4-5：浅克隆计划用于暂停时快照。
    /// 恢复时与可能已修改的计划做 diff 比较，判断哪些步骤需要重做。
    /// </summary>
    private static ExecutionPlan ClonePlanForDiff(ExecutionPlan source)
    {
        var clone = new ExecutionPlan
        {
            PlanId = source.PlanId,
            TaskId = source.TaskId,
            Version = source.Version,
            TaskSummary = source.TaskSummary,
            FinalGoal = source.FinalGoal,
            Status = source.Status,
            CreatedAt = source.CreatedAt,
            LastModifiedAt = source.LastModifiedAt,
        };

        foreach (var step in source.Steps)
        {
            clone.Steps.Add(new PlanStep
            {
                StepId = step.StepId,
                Sequence = step.Sequence,
                Title = step.Title,
                Description = step.Description,
                ExecutionMode = step.ExecutionMode,
                AgentTypeDescription = step.AgentTypeDescription,
                Status = step.Status,
                ResultSummary = step.ResultSummary,
                ResultDetail = step.ResultDetail,
                DependsOn = [.. step.DependsOn],
            });
        }

        return clone;
    }

    /// <summary>
    /// 执行单个步骤，含重试逻辑。
    /// </summary>
    private async Task ExecuteStepWithRetryAsync(
        string taskId, ExecutionPlan plan, PlanStep step, CancellationToken ct)
    {
        step.Status = PlanStepStatus.Running;
        step.StartedAt = DateTimeOffset.UtcNow;

        _publisher.Publish(Events.OnTaskStepStarted, new TaskStepEventArgs(
            taskId, DateTimeOffset.UtcNow, step.StepId, step.Sequence, step.Title, "running"));

        var maxRetries = step.MaxRetries > 0 ? step.MaxRetries : _options.DefaultRetryPolicy.MaxRetries;

        while (true)
        {
            try
            {
                // 单步超时
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(_options.PerStepTimeout);

                // 主智能体为这个步骤创建子智能体并委托执行
                // 注：LLM 并发节流已在 SubAgentRunner 层处理（每次 LLM 调用都通过 GlobalLlmThrottle）
                var result = await _orchestrator.ExecuteStepAsync(taskId, plan, step, stepCts.Token)
                    .ConfigureAwait(false);

                // 成功
                step.Status = PlanStepStatus.Completed;
                step.CompletedAt = DateTimeOffset.UtcNow;
                step.ResultSummary = result.Summary;
                step.ResultDetail = result.Detail;
                step.ProgressPercent = 100;

                _publisher.Publish(Events.OnTaskStepCompleted, new TaskStepEventArgs(
                    taskId, DateTimeOffset.UtcNow, step.StepId, step.Sequence,
                    step.Title, "completed", result.Summary));

                await _persistence.SaveStepResultAsync(taskId, step.StepId, result, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation("P4 步骤完成: {TaskId}/{StepId} ({Title})",
                    taskId, step.StepId, step.Title);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                step.RetryCount++;

                if (step.RetryCount <= maxRetries && IsRetryableError(ex))
                {
                    // 重试
                    step.Status = PlanStepStatus.Retrying;
                    var delay = CalculateBackoff(step.RetryCount);

                    _publisher.Publish(Events.OnTaskStepRetrying, new TaskStepRetryEventArgs(
                        taskId, DateTimeOffset.UtcNow, step.StepId, step.Sequence,
                        step.Title, step.RetryCount, maxRetries, ex.Message, (int)delay.TotalMilliseconds));

                    _logger.LogWarning(
                        "P4 步骤重试 ({RetryCount}/{MaxRetries}): {TaskId}/{StepId} — {Error}",
                        step.RetryCount, maxRetries, taskId, step.StepId, ex.Message);

                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                // 重试耗尽 → 标记失败
                step.Status = PlanStepStatus.Failed;
                step.ErrorMessage = ex.Message;

                _publisher.Publish(Events.OnTaskStepFailed, new TaskStepEventArgs(
                    taskId, DateTimeOffset.UtcNow, step.StepId, step.Sequence,
                    step.Title, "failed", ex.Message));

                _logger.LogError(ex, "P4 步骤失败: {TaskId}/{StepId} ({Title})",
                    taskId, step.StepId, step.Title);
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 辅助方法
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 计算指数退避延迟。
    /// </summary>
    private TimeSpan CalculateBackoff(int retryCount)
    {
        var policy = _options.DefaultRetryPolicy;

        if (policy.BackoffStrategy == "fixed")
            return TimeSpan.FromSeconds(policy.InitialDelaySeconds);

        // exponential: initialDelay * 2^(retryCount-1), capped at maxDelay
        var delaySeconds = policy.InitialDelaySeconds * Math.Pow(2, retryCount - 1);
        delaySeconds = Math.Min(delaySeconds, policy.MaxDelaySeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }

    /// <summary>
    /// 判断异常是否可重试。
    /// </summary>
    private bool IsRetryableError(Exception ex)
    {
        var retryableTypes = _options.DefaultRetryPolicy.RetryableErrorTypes;

        // 网络错误
        if (retryableTypes.Contains("network") && ex is HttpRequestException or TimeoutException)
            return true;

        // 限流错误（通常由 HTTP 429 引起）
        if (retryableTypes.Contains("rate_limit") &&
            ex.Message.Contains("429", StringComparison.Ordinal))
            return true;

        // 临时性服务端错误（5xx）
        if (retryableTypes.Contains("transient") &&
            (ex.Message.Contains("500", StringComparison.Ordinal) ||
             ex.Message.Contains("502", StringComparison.Ordinal) ||
             ex.Message.Contains("503", StringComparison.Ordinal)))
            return true;

        return false;
    }
}

/// <summary>
/// P4 运行中任务的运行期上下文。
/// 由 <see cref="TaskExecutionEngine._runningTasks"/> 持有。
///
/// P4-5：从 record 改为 sealed class，支持可变的暂停/恢复状态。
/// </summary>
internal sealed class RunningTaskEngineContext : IDisposable
{
    public RunningTaskEngineContext(
        string taskId, Task executionTask,
        CancellationTokenSource cts, DateTimeOffset startedAt)
    {
        TaskId = taskId;
        ExecutionTask = executionTask;
        Cts = cts;
        StartedAt = startedAt;
    }

    public string TaskId { get; }
    public Task ExecutionTask { get; }
    public CancellationTokenSource Cts { get; }
    public DateTimeOffset StartedAt { get; }

    // ── P4-5: 暂停/恢复状态 ──

    /// <summary>暂停请求标志（volatile，跨线程可见）。执行循环在步骤间检测此标志。</summary>
    public volatile bool PauseRequested;

    /// <summary>恢复信号。执行循环暂停后 await 此 TCS，ResumeTaskAsync 设置结果以唤醒。</summary>
    public TaskCompletionSource<bool> ResumeSignal { get; private set; } = new();

    /// <summary>暂停时的计划快照（用于恢复时做增量 diff 比较）。</summary>
    public ExecutionPlan? PausedPlanSnapshot { get; set; }

    /// <summary>重置恢复信号（恢复后为下一次暂停周期创建新 TCS）。</summary>
    public void ResetResumeSignal() => ResumeSignal = new TaskCompletionSource<bool>();

    // ── P4 用户确认计划状态 ──

    /// <summary>
    /// 计划确认信号。
    /// 引擎在阶段 2 结束后 await 此 TCS，等待用户确认或修改。
    /// - SetResult(null)：用户确认计划，进入执行阶段。
    /// - SetResult("修改意见")：用户请求修改计划，引擎重新生成。
    /// - SetCanceled()：取消任务。
    /// </summary>
    public TaskCompletionSource<string?> PlanConfirmationSignal { get; private set; } = new();

    /// <summary>标记是否正在等待用户确认计划（UI 用于展示确认按钮）。</summary>
    public volatile bool WaitingPlanConfirmation;

    /// <summary>重置计划确认信号（修改计划后再次等待确认）。</summary>
    public void ResetPlanConfirmationSignal() => PlanConfirmationSignal = new TaskCompletionSource<string?>();

    public void Dispose() => Cts.Dispose();
}
