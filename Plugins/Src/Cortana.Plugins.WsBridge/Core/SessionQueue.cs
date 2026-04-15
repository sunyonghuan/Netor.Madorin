namespace Cortana.Plugins.WsBridge.Core;

/// <summary>
/// 会话级串行执行队列。
/// 保证同一时刻只有一个 AI 请求在处理（send → 等 done/error → 下一条）。
/// </summary>
public sealed class SessionQueue : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _pending;

    /// <summary>
    /// 发送请求并等待 Cortana 回复完成（done/error）。
    /// </summary>
    public async Task SendAndWaitAsync(Func<CancellationToken, Task> sendAction, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await sendAction(ct);
            await _pending.Task.WaitAsync(ct);
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// 由 Cortana 侧收到 done/error 时调用，释放当前等待。
    /// </summary>
    public void SignalCompletion() => _pending?.TrySetResult(true);

    public void Dispose() => _gate.Dispose();
}
