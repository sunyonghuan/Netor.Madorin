namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// 阶段 3B：追踪一个正在运行的 Workflow 任务的运行期上下文。
/// 由 <c>WorkflowExecutor._runningTasks</c>（<c>ConcurrentDictionary&lt;string, RunningTaskContext&gt;</c>）持有。
///
/// 主要用途：
/// 1. <see cref="CancellationTokenSource"/>：用户调用 <c>CancelTaskAsync</c> 时通过它取消整个任务执行循环
/// 2. <see cref="ExecutionTask"/>：可被外部 <c>await</c> 等待任务结束（例如在测试或宿主关闭时）
/// 3. <see cref="StartedAt"/>：诊断/日志使用，便于排查长时间未完成的任务
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §3B.3。
/// </summary>
internal sealed record RunningTaskContext(
    string TaskId,
    Task ExecutionTask,
    CancellationTokenSource Cts,
    DateTimeOffset StartedAt) : IDisposable
{
    /// <summary>
    /// 释放底层 <see cref="CancellationTokenSource"/>。注意：调用方需自行确保 <see cref="ExecutionTask"/> 已结束。
    /// </summary>
    public void Dispose()
    {
        Cts.Dispose();
    }
}
