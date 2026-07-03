using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式输入路由器实现。
/// 根据意图分类结果，调用对应的执行器处理用户输入。
/// </summary>
public sealed class WorkModeInputRouter : IWorkModeInputRouter
{
    private readonly IIntentClassifier _intentClassifier;
    private readonly ICurrentSessionResolver _sessionResolver;
    private readonly WorkTaskService _taskService;
    private readonly WorkPendingInputService _pendingInputService;
    private readonly WorkflowExecutor _executor;

    public WorkModeInputRouter(
        IIntentClassifier intentClassifier,
        ICurrentSessionResolver sessionResolver,
        WorkTaskService taskService,
        WorkPendingInputService pendingInputService,
        WorkflowExecutor executor)
    {
        _intentClassifier = intentClassifier ?? throw new ArgumentNullException(nameof(intentClassifier));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task RouteAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        // 获取当前会话信息
        var sessionId = _sessionResolver.GetCurrentSessionId();
        if (sessionId is null)
        {
            // 无会话上下文，无法处理
            return;
        }

        // 检查是否有活跃任务
        var activeTask = _taskService.GetActiveTask(sessionId);
        var hasActiveTask = activeTask is not null;
        if (activeTask is not null && !string.IsNullOrWhiteSpace(activeTask.PendingRequestId))
        {
            await HandleResumeTaskAsync(input, activeTask, cancellationToken);
            return;
        }

        // 意图分类
        var intent = await _intentClassifier.ClassifyAsync(input, hasActiveTask, cancellationToken);

        // 根据意图路由
        switch (intent)
        {
            case WorkModeIntent.NewTask:
                await HandleNewTaskAsync(input, sessionId, cancellationToken);
                break;

            case WorkModeIntent.ContinueTask:
                await HandleContinueTaskAsync(input, activeTask!, cancellationToken);
                break;

            case WorkModeIntent.ResumeTask:
                await HandleResumeTaskAsync(input, activeTask!, cancellationToken);
                break;

            case WorkModeIntent.SoftPreemption:
                await HandleSoftPreemptionAsync(input, activeTask!, cancellationToken);
                break;

            case WorkModeIntent.CancelTask:
                await HandleCancelTaskAsync(activeTask!, cancellationToken);
                break;

            case WorkModeIntent.Unknown:
            default:
                // 兜底：当作继续任务处理
                if (hasActiveTask)
                    await HandleContinueTaskAsync(input, activeTask!, cancellationToken);
                else
                    await HandleNewTaskAsync(input, sessionId, cancellationToken);
                break;
        }
    }

    private Task HandleNewTaskAsync(string input, string sessionId, CancellationToken cancellationToken)
    {
        // 当前 UI 主路径由 WorkModeInputVm 创建任务。路由器保持无副作用，避免重复建任务。
        return Task.CompletedTask;
    }

    private Task HandleContinueTaskAsync(string input, WorkTaskEntity activeTask, CancellationToken cancellationToken)
    {
        return _executor.ContinueAsync(activeTask.Id, input, cancellationToken: cancellationToken);
    }

    private Task HandleResumeTaskAsync(string input, WorkTaskEntity activeTask, CancellationToken cancellationToken)
    {
        return _executor.ResumeAsync(activeTask.Id, input, cancellationToken);
    }

    private Task HandleSoftPreemptionAsync(string input, WorkTaskEntity activeTask, CancellationToken cancellationToken)
    {
        _pendingInputService.Enqueue(activeTask.Id, input);
        _taskService.SetPreemption(activeTask.Id, true);
        return Task.CompletedTask;
    }

    private Task HandleCancelTaskAsync(WorkTaskEntity activeTask, CancellationToken cancellationToken)
    {
        return _executor.CancelAsync(activeTask.Id, cancellationToken);
    }
}
