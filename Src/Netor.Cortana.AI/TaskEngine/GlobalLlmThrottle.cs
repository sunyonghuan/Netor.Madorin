namespace Netor.Cortana.AI.TaskEngine;

/// <summary>
/// 全局 LLM 并发限流器。基于 SemaphoreSlim 实现，防止同时发起过多 LLM 请求。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/07-P4补充-资源管理与工具授权.md §1。
/// </summary>
public sealed class GlobalLlmThrottle : IDisposable
{
    private SemaphoreSlim _semaphore;
    private readonly object _lock = new();

    /// <summary>当前最大并发数。</summary>
    public int MaxConcurrency { get; private set; }

    /// <summary>当前可用信号量数。</summary>
    public int CurrentCount => _semaphore.CurrentCount;

    /// <summary>当前正在使用的 LLM 槽位数。</summary>
    public int ActiveCount => MaxConcurrency - _semaphore.CurrentCount;

    public GlobalLlmThrottle(int maxConcurrency = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// 获取一个 LLM 执行槽位。到达上限时阻塞等待。
    /// 返回的 <see cref="IDisposable"/> 在 Dispose 时释放槽位。
    /// </summary>
    /// <param name="ct">取消令牌（任务取消时解除等待）。</param>
    /// <returns>释放槽位的句柄。</returns>
    /// <exception cref="OperationCanceledException">ct 取消时抛出。</exception>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new ReleaseHandle(_semaphore);
    }

    /// <summary>
    /// 尝试立即获取一个槽位（不阻塞）。
    /// </summary>
    /// <param name="handle">成功时返回释放句柄。</param>
    /// <returns>true 表示成功获取，false 表示当前已满。</returns>
    public bool TryAcquire(out IDisposable? handle)
    {
        if (_semaphore.Wait(TimeSpan.Zero))
        {
            handle = new ReleaseHandle(_semaphore);
            return true;
        }
        handle = null;
        return false;
    }

    /// <summary>
    /// 动态调整最大并发数。已持有的槽位不受影响，新上限在下一次 Acquire 时生效。
    /// </summary>
    public void UpdateMaxConcurrency(int newMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(newMax, 1);

        lock (_lock)
        {
            if (newMax == MaxConcurrency) return;

            // 重建 SemaphoreSlim（简单策略：新信号量初始值 = newMax - ActiveCount，最小为 0）
            var active = MaxConcurrency - _semaphore.CurrentCount;
            var initialCount = Math.Max(0, newMax - active);
            var oldSemaphore = _semaphore;
            _semaphore = new SemaphoreSlim(initialCount, newMax);
            MaxConcurrency = newMax;
            oldSemaphore.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    /// <summary>释放一个 LLM 槽位的句柄。</summary>
    private sealed class ReleaseHandle(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore 已被 UpdateMaxConcurrency 替换或 Throttle 已 Dispose，忽略
                }
            }
        }
    }
}
