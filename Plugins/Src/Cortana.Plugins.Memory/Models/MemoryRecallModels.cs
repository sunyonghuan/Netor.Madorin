namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示一次记忆召回请求。
/// </summary>
public sealed class MemoryRecallRequest
{
    /// <summary>
    /// 召回请求标识。
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 发起召回的智能体标识。
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// 召回请求所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// 召回查询文本。
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// 查询意图。
    /// </summary>
    public string? QueryIntent { get; init; }

    /// <summary>
    /// 触发召回的来源。
    /// </summary>
    public string? TriggerSource { get; init; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// 表示一次记忆召回结果。
/// </summary>
public sealed class MemoryRecallResult
{
    /// <summary>
    /// 召回请求标识。
    /// </summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// 命中的记忆窗口集合。
    /// </summary>
    public IReadOnlyList<MemoryRecallWindow> Windows { get; init; } = [];

    /// <summary>
    /// 命中的记忆项集合。
    /// </summary>
    public IReadOnlyList<MemoryRecallItem> Items { get; init; } = [];

    /// <summary>
    /// 本次召回整体可信度。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 本次召回摘要。
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// 表示提供给上层的一组记忆窗口。
/// </summary>
public sealed class MemoryRecallWindow
{
    /// <summary>
    /// 窗口标题。
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 窗口主题。
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// 窗口内记忆项集合。
    /// </summary>
    public IReadOnlyList<MemoryRecallItem> Items { get; init; } = [];
}

/// <summary>
/// 表示一次召回命中的单条记忆。
/// </summary>
public sealed class MemoryRecallItem
{
    /// <summary>
    /// 记忆标识。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 记忆类型，fragment 或 abstraction。
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// 所属智能体标识。
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// 所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }

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
    /// 详细内容或核心陈述。
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 可信度评分。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 显著性评分。
    /// </summary>
    public double SalienceScore { get; init; }

    /// <summary>
    /// 保留评分。
    /// </summary>
    public double RetentionScore { get; init; }

    /// <summary>
    /// 被访问次数。
    /// </summary>
    public int AccessCount { get; init; }

    /// <summary>
    /// 综合召回评分。
    /// </summary>
    public double RecallScore { get; init; }

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
