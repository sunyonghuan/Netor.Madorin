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
    private readonly GlobalLlmThrottle _throttle;
    private readonly TaskEngineOptions _options;
    private readonly ILogger<TaskExecutionEngine> _logger;

    /// <summary>运行中的任务上下文。</summary>
    private readonly ConcurrentDictionary<string, RunningTaskEngineContext> _runningTasks = new();

    public TaskExecutionEngine(
        IOrchestratorAgent orchestrator,
        IStepScheduler scheduler,
        IPlanPersistence persistence,
        IPublisher publisher,
        GlobalLlmThrottle throttle,
        TaskEngineOptions options,
        ILogger<TaskExecutionEngine> logger)
    {
        _orchestrator = orchestrator;
        _scheduler = scheduler;
        _persistence = persistence;
        _publisher = publisher;
        _throttle = throttle;
        _options = options;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // IHostedService
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 应用启动时调用。扫描持久化目录中未完成的任务，尝试恢复或标记为失败。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("P4 任务执行引擎已启动（MaxParallel={MaxParallel}, MaxLlm={MaxLlm}）",
            _options.MaxParallelSteps, _options.MaxLlmConcurrency);

        // TODO [P4-2]: 实现断点恢复 — 扫描 checkpoint.json 并恢复中断的任务
        return Task.CompletedTask;
    }

    /// <summary>
    /// 应用关闭时调用。取消所有运行中任务并等待其结束。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("P4 任务执行引擎正在关闭，取消 {Count} 个运行中任务...", _runningTasks.Count);

        // 取消所有运行中任务
        foreach (var (_, ctx) in _runningTasks)
        {
            await ctx.Cts.CancelAsync().ConfigureAwait(false);
        }

        // 等待所有任务结束（带超时）
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

        // 清理资源
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
    /// <param name="ct">调用方取消令牌（仅用于启动阶段校验，不影响任务运行）。</param>
    /// <returns>新任务 ID。</returns>
    public async Task<string> StartTaskAsync(string userInput, string workspaceId, string? templateId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var taskId = Guid.NewGuid().ToString("N");

        // P4-2: 创建执行运行（生成 Run ID + 创建目录结构 + 写入 task.json / run.json / latest-run）
        var taskTitle = userInput.Length > 50 ? userInput[..50] + "…" : userInput;
        await _persistence.CreateRunAsync(taskId, taskTitle, ct).ConfigureAwait(false);

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
    /// 取消运行中的任务。
    /// </summary>
    /// <returns>true 表示已发取消信号；false 表示任务不存在或已结束。</returns>
    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct)
    {
        if (!_runningTasks.TryGetValue(taskId, out var context))
            return false;

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

            // 等待用户确认（P4-5 实现完整的多轮修改流程，P4-1 直接 auto-confirm）
            plan.Status = PlanStatus.Confirmed;
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

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

            await _orchestrator.RunValidationPhaseAsync(taskId, plan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskPhaseCompleted,
                new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "validating"));

            // ── 完成 ──
            plan.Status = PlanStatus.Completed;
            await _persistence.SavePlanAsync(plan, ct).ConfigureAwait(false);

            _publisher.Publish(Events.OnTaskEngineCompleted,
                new TaskLifecycleEventArgs(taskId, DateTimeOffset.UtcNow));

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

                // 恢复后重新加载计划（暂停期间可能被用户修改过）
                var reloadedPlan = await _persistence.LoadPlanAsync(taskId, ct).ConfigureAwait(false);
                if (reloadedPlan is not null) plan = reloadedPlan;
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
                // 通过 LLM 限流器获取槽位
                using (await _throttle.AcquireAsync(ct).ConfigureAwait(false))
                {
                    // 单步超时
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(_options.PerStepTimeout);

                    // 主智能体为这个步骤创建子智能体并委托执行
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

    public void Dispose() => Cts.Dispose();
}
