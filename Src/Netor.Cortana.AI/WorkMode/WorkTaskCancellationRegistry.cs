using System.Collections.Concurrent;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作任务运行时取消令牌注册表。
/// 用于把 UI 取消、AI cancel_task 工具和执行器流式循环连接到同一个取消信号。
/// </summary>
public sealed class WorkTaskCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningOrchestrators = new();

    public bool TryRegister(string taskId, CancellationTokenSource cts)
        => _runningTasks.TryAdd(taskId, cts);

    public bool TryUnregister(string taskId)
        => _runningTasks.TryRemove(taskId, out _);

    public bool TryRegisterOrchestrator(string taskId, CancellationTokenSource cts)
        => _runningOrchestrators.TryAdd(taskId, cts);

    public bool TryUnregisterOrchestrator(string taskId)
        => _runningOrchestrators.TryRemove(taskId, out _);

    public async Task CancelAsync(string taskId)
    {
        if (_runningTasks.TryGetValue(taskId, out var taskCts))
        {
            await taskCts.CancelAsync().ConfigureAwait(false);
        }

        if (_runningOrchestrators.TryGetValue(taskId, out var orchestratorCts))
        {
            await orchestratorCts.CancelAsync().ConfigureAwait(false);
        }
    }
}
