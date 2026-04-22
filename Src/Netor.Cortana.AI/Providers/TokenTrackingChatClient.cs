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

    /// <summary>
    /// Usage 上报抑制计数（支持嵌套）。>0 时 <see cref="RecordUsage"/> 直接忽略。
    /// 用于压缩/标题生成等"后台 LLM 调用"期间，防止它们的 usage 覆盖主对话的进度条。
    /// </summary>
    private int _suppressDepth;

    /// <summary>
    /// 用量观察者：每当本 Client 收到 <see cref="UsageDetails"/> 时会被回调。
    /// 用于把 token 状态上报到工厂/外层持久容器，使 UI 显示不随 ChatClient 重建而丢失。
    /// </summary>
    private readonly Action<UsageDetails>? _usageObserver;

    internal TokenTrackingChatClient(
        IChatClient innerClient,
        long maxContextTokens,
        Action<UsageDetails>? usageObserver = null)
        : base(innerClient)
    {
        MaxContextTokens = maxContextTokens <= 0 ? 128000 : maxContextTokens;
        _usageObserver = usageObserver;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _lastInputTokens, 0);
        Interlocked.Exchange(ref _totalOutputTokens, 0);
    }

    /// <summary>
    /// 开启一个作用域：在作用域内的所有 LLM 调用产生的 UsageDetails 都会被忽略，
    /// 不会覆盖 <see cref="LastInputTokens"/>，也不会触发 observer。
    /// 典型场景：历史压缩、会话标题生成等后台调用借用本 ChatClient，但其 token 不应显示到主进度条。
    /// </summary>
    public IDisposable SuppressUsage() => new SuppressScope(this);

    private sealed class SuppressScope : IDisposable
    {
        private readonly TokenTrackingChatClient _owner;
        private int _disposed;
        public SuppressScope(TokenTrackingChatClient owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._suppressDepth);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _owner._suppressDepth);
        }
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
        // 流式期间累积 usage：取 InputToken 的最大值、OutputToken 累加，流结束统一提交一次
        long? pendingInput = null;
        long pendingOutput = 0;
        UsageDetails? lastUsage = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, ct))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    var details = usage.Details;
                    if (details.InputTokenCount is { } i && (pendingInput is null || i > pendingInput))
                        pendingInput = i;
                    if (details.OutputTokenCount is { } o)
                        pendingOutput += o;
                    lastUsage = details;
                }
            }
            yield return update;
        }

        if (lastUsage is not null)
        {
            // 构造合并后的 UsageDetails 统一上报一次
            var merged = new UsageDetails
            {
                InputTokenCount = pendingInput ?? lastUsage.InputTokenCount,
                OutputTokenCount = pendingOutput > 0 ? pendingOutput : lastUsage.OutputTokenCount,
                TotalTokenCount = lastUsage.TotalTokenCount,
                AdditionalCounts = lastUsage.AdditionalCounts
            };
            RecordUsage(merged);
        }
    }

    private void RecordUsage(UsageDetails? usage)
    {
        if (usage is null) return;
        if (Volatile.Read(ref _suppressDepth) > 0) return;

        // 输入 token：覆盖（取最新值，因为它反映当前上下文大小）
        Interlocked.Exchange(ref _lastInputTokens, usage.InputTokenCount ?? 0);
        // 输出 token：累加（用于成本）
        Interlocked.Add(ref _totalOutputTokens, usage.OutputTokenCount ?? 0);

        // 上报到外部观察者（若有）
        try { _usageObserver?.Invoke(usage); }
        catch { /* 观察者异常不影响主流程 */ }
    }
}