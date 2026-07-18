namespace Netor.Cortana.AI;

/// <summary>
/// 统一处理当前活跃 turn 的停止、取消与新 turn 启动前的空闲收敛。
/// </summary>
public sealed class ChatTurnInterruptionCoordinator(
    ChatTurnCancellationRegistry turnRegistry,
    ChatTurnLifecycleCoordinator turnLifecycleCoordinator,
    ChatTurnExecutor turnExecutor)
{
    public bool IsRunning => turnRegistry.HasActiveTurn();

    public void StopCurrentTurn()
    {
        if (!IsRunning)
        {
            return;
        }

        _ = turnRegistry.CancelCurrentTurn();
    }

    public void CancelCurrentTurn()
    {
        _ = Task.Run(() => CancelCurrentTurnAsync(CancellationToken.None));
    }

    public async Task CancelCurrentTurnAsync(CancellationToken cancellationToken = default)
    {
        await turnExecutor.CancelCurrentTurnAsync(
            turnLifecycleCoordinator.NotifyChannelsCancelledAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureReadyForNextTurnAsync(
        CancellationToken cancellationToken,
        string? runningLogMessage = null)
    {
        if (!IsRunning)
        {
            return;
        }

        await CancelCurrentTurnAsync(cancellationToken).ConfigureAwait(false);
        await turnLifecycleCoordinator.WaitForCurrentRunToStopAsync(cancellationToken).ConfigureAwait(false);
    }
}
