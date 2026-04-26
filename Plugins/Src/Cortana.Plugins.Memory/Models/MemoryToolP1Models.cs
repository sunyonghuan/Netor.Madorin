namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示人工写入记忆请求。
/// </summary>
public sealed class MemoryAddNoteRequest
{
    /// <summary>
    /// 发起写入的智能体标识。
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// 记忆所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// 需要写入的记忆内容。
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 记忆类型，例如 fact、preference、task 或 constraint。
    /// </summary>
    public string MemoryType { get; init; } = string.Empty;

    /// <summary>
    /// 记忆主题。
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>
    /// 用户明确授权写入的原因。
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 是否已获得用户明确授权。
    /// </summary>
    public bool UserConfirmed { get; init; }

    /// <summary>
    /// 触发来源。
    /// </summary>
    public string? TriggerSource { get; init; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// 表示人工写入记忆结果。
/// </summary>
public sealed class MemoryAddNoteResult
{
    /// <summary>
    /// 写入的记忆标识。
    /// </summary>
    public string MemoryId { get; init; } = string.Empty;

    /// <summary>
    /// 写入的记忆类别。
    /// </summary>
    public string Kind { get; init; } = "fragment";

    /// <summary>
    /// 记忆类型。
    /// </summary>
    public string MemoryType { get; init; } = string.Empty;

    /// <summary>
    /// 记忆主题。
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// 生命周期状态。
    /// </summary>
    public string LifecycleState { get; init; } = string.Empty;

    /// <summary>
    /// 确认状态。
    /// </summary>
    public string ConfirmationState { get; init; } = string.Empty;

    /// <summary>
    /// 审计记录标识。
    /// </summary>
    public string MutationId { get; init; } = string.Empty;

    /// <summary>
    /// 写入时间。
    /// </summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>
    /// 结果摘要。
    /// </summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// 表示最近记忆列表请求。
/// </summary>
public sealed class MemoryListRecentRequest
{
    /// <summary>
    /// 智能体标识。
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// 工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// 最多返回数量。
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// 记忆类别过滤，支持 fragment、abstraction 或空值。
    /// </summary>
    public string? Kind { get; init; }
}

/// <summary>
/// 表示最近记忆列表结果。
/// </summary>
public sealed class MemoryListRecentResult
{
    /// <summary>
    /// 返回的记忆数量。
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// 本次实际应用的数量上限。
    /// </summary>
    public int Limit { get; init; }

    /// <summary>
    /// 记忆类别过滤。
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// 最近记忆项集合。
    /// </summary>
    public IReadOnlyList<MemoryRecentItem> Items { get; init; } = [];
}

/// <summary>
/// 表示一条可展示的最近记忆。
/// </summary>
public sealed class MemoryRecentItem
{
    /// <summary>
    /// 记忆标识。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 记忆类别，fragment 或 abstraction。
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// 主题或抽象类型。
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// 标题。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 摘要。
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// 可信度评分。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 生命周期状态。
    /// </summary>
    public string LifecycleState { get; init; } = string.Empty;

    /// <summary>
    /// 确认状态。
    /// </summary>
    public string ConfirmationState { get; init; } = string.Empty;

    /// <summary>
    /// 更新时间。
    /// </summary>
    public string UpdatedAt { get; init; } = string.Empty;
}
