namespace Netor.Cortana.Entitys;

/// <summary>
/// 主软件托管的背景任务状态（持久化为 TEXT 列）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §8.4。
/// </summary>
public static class WorkBackgroundJobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// 主软件托管的背景任务种类（持久化为 TEXT 列）。
/// v1.0 仅 <see cref="SubAgent"/>；v1.1+ 可扩展（如批量分析、异步审核）。
/// </summary>
public static class WorkBackgroundJobKinds
{
    /// <summary>子智能体背景执行（v1.0 唯一种类）。</summary>
    public const string SubAgent = "subagent";
}

/// <summary>
/// 主软件托管的背景任务记录（WorkBackgroundJobs 表）。
/// 仅服务"主软件托管的背景任务"——不用于插件长任务（插件自治）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §3.6。
/// </summary>
public sealed class WorkBackgroundJobEntity
{
    /// <summary>背景任务 ID（jobId，GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>父 WorkTask。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>任务种类，见 <see cref="WorkBackgroundJobKinds"/>。</summary>
    public string JobKind { get; set; } = string.Empty;

    /// <summary>子智能体显示名 / 任务来源标识。</summary>
    public string? OwnerName { get; set; }

    /// <summary>状态，见 <see cref="WorkBackgroundJobStates"/>。</summary>
    public string State { get; set; } = WorkBackgroundJobStates.Pending;

    /// <summary>启动参数 JSON（按 JobKind 解析）。</summary>
    public string InputJson { get; set; } = string.Empty;

    /// <summary>"正在分析第 12 个文件..." 给 AI 看的人话进度。</summary>
    public string? ProgressDescription { get; set; }

    /// <summary>State=completed 时的结果 JSON。</summary>
    public string? ResultJson { get; set; }

    /// <summary>State=failed 时的错误信息。</summary>
    public string? Error { get; set; }

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>最后更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>完成时间（Unix 毫秒）。</summary>
    public long? CompletedAt { get; set; }
}
