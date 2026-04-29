using System.Runtime.CompilerServices;

namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy Agent 后端基类。
/// 提供请求统计、基础校验和错误封装；具体 AI 调用由派生类实现。
/// </summary>
public abstract class AiProxyAgentBackendBase : IAiProxyAgentBackend
{
    protected AiProxyAgentBackendBase(ProxyUsageTracker usageTracker)
    {
        UsageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
    }

    /// <summary>
    /// Proxy 专用用量统计器。
    /// </summary>
    protected ProxyUsageTracker UsageTracker { get; }

    public abstract IReadOnlyList<AiProxyModelDescriptor> ListModels();

    public async IAsyncEnumerable<AiProxyChatDelta> ChatAsync(
        AiProxyAgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var succeeded = false;
        string? error = null;
        UsageTracker.MarkRequestStarted();

        try
        {
            await foreach (var delta in ChatCoreAsync(request, cancellationToken).WithCancellation(cancellationToken))
            {
                UsageTracker.RecordUsage(delta.InputTokens, delta.OutputTokens);

                if (!string.IsNullOrWhiteSpace(delta.ErrorMessage))
                {
                    error = delta.ErrorMessage;
                }

                if (delta.Done && delta.FinishReason != AiProxyFinishReason.Error)
                {
                    succeeded = true;
                }

                yield return delta;
            }

            succeeded = error is null;
        }
        finally
        {
            UsageTracker.MarkRequestCompleted(succeeded, error);
        }
    }

    /// <summary>
    /// 派生类实现实际 AI 调用。
    /// </summary>
    protected abstract IAsyncEnumerable<AiProxyChatDelta> ChatCoreAsync(
        AiProxyAgentRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 基础请求校验。
    /// </summary>
    protected virtual void ValidateRequest(AiProxyAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new ArgumentException("Proxy request id cannot be empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("Proxy model cannot be empty.", nameof(request));
        }

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("Proxy request must contain at least one message.", nameof(request));
        }
    }
}
