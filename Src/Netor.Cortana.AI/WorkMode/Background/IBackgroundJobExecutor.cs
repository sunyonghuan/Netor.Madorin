namespace Netor.Cortana.AI.WorkMode.Background;

/// <summary>
/// 背景任务执行器接口。
/// 主软件托管的背景任务（如子智能体背景执行）通过此接口统一管理。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §八。
/// </summary>
public interface IBackgroundJobExecutor
{
    /// <summary>
    /// 执行器支持的任务类型（如 "subagent"）。
    /// </summary>
    string JobKind { get; }

    /// <summary>
    /// 启动背景任务。
    /// </summary>
    /// <param name="request">启动请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务 ID。</returns>
    Task<string> StartAsync(BackgroundJobStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 等待背景任务完成或超时。
    /// </summary>
    /// <param name="jobId">任务 ID。</param>
    /// <param name="maxSeconds">最大等待秒数（上限 60 秒）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务状态。</returns>
    Task<BackgroundJobStatus> WaitForAsync(string jobId, int maxSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消背景任务。
    /// </summary>
    /// <param name="jobId">任务 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 背景任务启动请求。
/// </summary>
/// <param name="TaskId">所属工作任务 ID。</param>
/// <param name="InputJson">输入参数 JSON（由具体执行器解析）。</param>
/// <param name="OwnerName">任务发起者名称（如子智能体名称）。</param>
public sealed record BackgroundJobStartRequest(
    string TaskId,
    string InputJson,
    string? OwnerName = null);

/// <summary>
/// 子智能体背景任务启动参数。
/// </summary>
public sealed record SubAgentJobInput(
    string AgentId,
    string Query,
    string[]? AttachmentPaths = null,
    string[]? AttachmentDescriptions = null,
    string? MainProviderId = null,
    string? MainModelId = null);

/// <summary>
/// 背景任务状态。
/// </summary>
/// <param name="JobId">任务 ID。</param>
/// <param name="State">任务状态。</param>
/// <param name="ProgressDescription">进度描述。</param>
/// <param name="ResultJson">结果 JSON（completed 时有值）。</param>
/// <param name="Error">错误信息（failed 时有值）。</param>
public sealed record BackgroundJobStatus(
    string JobId,
    BackgroundJobState State,
    string? ProgressDescription = null,
    string? ResultJson = null,
    string? Error = null);

/// <summary>
/// 背景任务状态枚举。
/// </summary>
public enum BackgroundJobState
{
    /// <summary>等待中。</summary>
    Pending,

    /// <summary>运行中。</summary>
    Running,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>已失败。</summary>
    Failed,

    /// <summary>已取消。</summary>
    Cancelled
}
