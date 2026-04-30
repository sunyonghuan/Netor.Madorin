namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy 专用用量统计器。
/// 该统计器独立于主聊天窗口，避免代理调用覆盖 UI 主会话 Token 状态。
/// </summary>
public sealed class ProxyUsageTracker
{
    private long _lastInputTokens;
    private long _totalOutputTokens;
    private long _maxContextTokens = 128_000;
    private long _totalRequests;
    private long _activeRequests;
    private long _succeededRequests;
    private long _failedRequests;
    private string _lastError = string.Empty;
    private string _lastModelName = string.Empty;

    /// <summary>
    /// 用量变化事件。UI 可订阅该事件刷新 ProxyWindow。
    /// </summary>
    public event Action? UsageChanged;

    public long LastInputTokens => Volatile.Read(ref _lastInputTokens);

    public long TotalOutputTokens => Volatile.Read(ref _totalOutputTokens);

    public long MaxContextTokens => Volatile.Read(ref _maxContextTokens);

    public double ContextUsageRatio
    {
        get
        {
            var max = MaxContextTokens;
            return max > 0 ? (double)LastInputTokens / max : 0d;
        }
    }

    public long TotalRequests => Volatile.Read(ref _totalRequests);

    public long ActiveRequests => Volatile.Read(ref _activeRequests);

    public long SucceededRequests => Volatile.Read(ref _succeededRequests);

    public long FailedRequests => Volatile.Read(ref _failedRequests);

    public string LastError => Volatile.Read(ref _lastError) ?? string.Empty;

    public string LastModelName => Volatile.Read(ref _lastModelName) ?? string.Empty;

    /// <summary>
    /// 标记请求开始。
    /// </summary>
    public void MarkRequestStarted()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _activeRequests);
        RaiseChanged();
    }

    /// <summary>
    /// 标记请求完成。
    /// </summary>
    public void MarkRequestCompleted(bool succeeded, string? error = null)
    {
        Interlocked.Decrement(ref _activeRequests);

        if (succeeded)
        {
            Interlocked.Increment(ref _succeededRequests);
        }
        else
        {
            Interlocked.Increment(ref _failedRequests);
            if (!string.IsNullOrWhiteSpace(error))
            {
                Volatile.Write(ref _lastError, error);
            }
        }

        RaiseChanged();
    }

    /// <summary>
    /// 更新上下文和输出用量。
    /// </summary>
    public void RecordUsage(long? inputTokens, long? outputTokens, long? maxContextTokens = null)
    {
        if (inputTokens is > 0)
        {
            Interlocked.Exchange(ref _lastInputTokens, inputTokens.Value);
        }

        if (outputTokens is > 0)
        {
            Interlocked.Add(ref _totalOutputTokens, outputTokens.Value);
        }

        if (maxContextTokens is > 0)
        {
            Interlocked.Exchange(ref _maxContextTokens, maxContextTokens.Value);
        }

        RaiseChanged();
    }

    /// <summary>
    /// 记录最近一次实际请求使用的模型，并同步该模型的上下文窗口上限。
    /// </summary>
    public void RecordModel(string? modelName, long? maxContextTokens = null)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            Volatile.Write(ref _lastModelName, modelName.Trim());
        }

        if (maxContextTokens is > 0)
        {
            Interlocked.Exchange(ref _maxContextTokens, maxContextTokens.Value);
        }

        RaiseChanged();
    }

    /// <summary>
    /// 重置统计数据。
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastInputTokens, 0);
        Interlocked.Exchange(ref _totalOutputTokens, 0);
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _activeRequests, 0);
        Interlocked.Exchange(ref _succeededRequests, 0);
        Interlocked.Exchange(ref _failedRequests, 0);
        Volatile.Write(ref _lastError, string.Empty);
        Volatile.Write(ref _lastModelName, string.Empty);
        RaiseChanged();
    }

    /// <summary>
    /// 获取当前用量快照。
    /// </summary>
    public AiProxyUsageSnapshot GetSnapshot() => new(
        LastInputTokens,
        TotalOutputTokens,
        MaxContextTokens,
        ContextUsageRatio,
        TotalRequests,
        ActiveRequests,
        SucceededRequests,
        FailedRequests,
        LastError,
        LastModelName);

    private void RaiseChanged()
    {
        try { UsageChanged?.Invoke(); }
        catch { /* 统计事件不能影响代理主流程 */ }
    }
}
