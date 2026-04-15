using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace Netor.Cortana.AI.Providers;

public class TokenTrackingChatClient : DelegatingChatClient
{
    /// <summary>最近一次调用的输入 token 数（= 当前上下文窗口占用）</summary>
    public long LastInputTokens => Volatile.Read(ref _lastInputTokens);

    /// <summary>本次对话累计输出 token（用于成本估算）</summary>
    public long TotalOutputTokens => Volatile.Read(ref _totalOutputTokens);

    /// <summary>模型上下文窗口最大值</summary>
    public long MaxContextTokens { get; } = 128000;

    /// <summary>上下文使用比例 0.0 ~ 1.0</summary>
    public double ContextUsageRatio =>
        MaxContextTokens > 0 ? (double)LastInputTokens / MaxContextTokens : 0;

    private long _lastInputTokens;
    private long _totalOutputTokens;

    internal TokenTrackingChatClient(IChatClient innerClient, long maxContextTokens)
        : base(innerClient)
    {
        MaxContextTokens = maxContextTokens <= 0 ? 128000 : maxContextTokens;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _lastInputTokens, 0);
        Interlocked.Exchange(ref _totalOutputTokens, 0);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct);
        RecordUsage(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, ct))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    RecordUsage(usage.Details);
                }
            }
            yield return update;
        }
    }

    private void RecordUsage(UsageDetails? usage)
    {
        if (usage is null) return;
        // 输入 token：覆盖（取最新值，因为它反映当前上下文大小）
        Interlocked.Exchange(ref _lastInputTokens, usage.InputTokenCount ?? 0);
        // 输出 token：累加（用于成本）
        Interlocked.Add(ref _totalOutputTokens, usage.OutputTokenCount ?? 0);
    }
}