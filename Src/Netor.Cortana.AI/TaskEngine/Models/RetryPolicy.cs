namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 重试策略配置。
/// 每步失败后根据此策略决定是否重试、延迟多久。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §6.4。
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>最大重试次数（每步独立计数）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// 重试间隔策略：
    /// <list type="bullet">
    ///   <item><c>"exponential"</c> — 指数退避（默认）</item>
    ///   <item><c>"fixed"</c> — 固定间隔</item>
    /// </list>
    /// </summary>
    public string BackoffStrategy { get; init; } = "exponential";

    /// <summary>初始间隔（秒）。</summary>
    public int InitialDelaySeconds { get; init; } = 2;

    /// <summary>最大间隔（秒）。</summary>
    public int MaxDelaySeconds { get; init; } = 30;

    /// <summary>
    /// 可重试的错误类型：
    /// <list type="bullet">
    ///   <item><c>"network"</c> — 网络超时/连接失败</item>
    ///   <item><c>"rate_limit"</c> — API 限流</item>
    ///   <item><c>"transient"</c> — 临时性服务端错误</item>
    /// </list>
    /// 不可重试：逻辑错误、权限错误、输入无效。
    /// </summary>
    public List<string> RetryableErrorTypes { get; init; } = ["network", "rate_limit", "transient"];
}
