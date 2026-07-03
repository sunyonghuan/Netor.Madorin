using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys.Memory;

using System.Collections.Concurrent;

namespace Netor.Cortana.Networks;

/// <summary>
/// 封装长期记忆供应请求的 pending 生命周期和响应匹配逻辑。
/// </summary>
internal sealed class PluginBusMemorySupplyDispatcher(ILogger logger)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MemoryContextSupplyPackage?>> _pendingRequests = new();

    /// <summary>
    /// 创建并登记一个长期记忆供应 pending 请求。
    /// </summary>
    /// <param name="requestId">请求标识。</param>
    /// <returns>登记成功的 pending；requestId 冲突时返回 <see langword="null"/>。</returns>
    public TaskCompletionSource<MemoryContextSupplyPackage?>? CreatePending(string requestId)
    {
        var pending = new TaskCompletionSource<MemoryContextSupplyPackage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pendingRequests.TryAdd(requestId, pending))
        {
            return pending;
        }

        logger.LogDebug("长期记忆供应请求 requestId 冲突，已降级为空。RequestId={RequestId}", requestId);
        return null;
    }

    /// <summary>
    /// 等待指定长期记忆供应请求完成。
    /// </summary>
    public async Task<MemoryContextSupplyPackage?> WaitAsync(
        string requestId,
        TaskCompletionSource<MemoryContextSupplyPackage?> pending,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            await using var registration = timeoutCts.Token.Register(static state =>
            {
                var source = (TaskCompletionSource<MemoryContextSupplyPackage?>)state!;
                source.TrySetResult(null);
            }, pending);

            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// 取消并移除指定 pending 请求，供发送失败或发送前取消时清理资源。
    /// </summary>
    /// <param name="requestId">请求标识。</param>
    public void CancelPending(string requestId)
    {
        if (_pendingRequests.TryRemove(requestId, out var pending))
        {
            pending.TrySetResult(null);
        }
    }

    /// <summary>
    /// 根据供应包完成对应请求。
    /// </summary>
    public void CompletePackage(MemoryContextSupplyPackage package)
    {
        if (_pendingRequests.TryRemove(package.RequestId, out var pending))
        {
            pending.TrySetResult(package);
        }
    }

    /// <summary>
    /// 根据供应错误完成对应请求。
    /// </summary>
    public void CompleteError(MemoryContextSupplyError error)
    {
        logger.LogDebug(
            "长期记忆供应返回错误：RequestId={RequestId}, Code={Code}, Message={Message}, Retryable={Retryable}",
            error.RequestId,
            error.Code,
            error.Message,
            error.Retryable);

        if (_pendingRequests.TryRemove(error.RequestId!, out var pending))
        {
            pending.TrySetResult(null);
        }
    }

    /// <summary>
    /// 取消所有等待中的请求。
    /// </summary>
    public void CancelAll()
    {
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetResult(null);
        }

        _pendingRequests.Clear();
    }
}
