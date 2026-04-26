namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示一次主动记忆供应请求。
/// </summary>
public sealed class MemorySupplyRequest
{
    /// <summary>
    /// 供应请求标识。
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 发起供应的智能体标识。
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// 请求所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// 当前供应场景。
    /// </summary>
    public string? Scenario { get; init; }

    /// <summary>
    /// 当前任务描述。
    /// </summary>
    public string? CurrentTask { get; init; }

    /// <summary>
    /// 最近消息摘要或文本片段。
    /// </summary>
    public IReadOnlyList<string> RecentMessages { get; init; } = [];

    /// <summary>
    /// 触发供应的来源。
    /// </summary>
    public string? TriggerSource { get; init; }

    /// <summary>
    /// 本次最多供应的记忆数量。
    /// </summary>
    public int? MaxMemoryCount { get; init; }

    /// <summary>
    /// 本次供应的最大 Token 预算。
    /// </summary>
    public int? MaxTokenBudget { get; init; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// 表示一次主动记忆供应结果。
/// </summary>
public sealed class MemorySupplyResult
{
    /// <summary>
    /// 供应请求标识。
    /// </summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// 本次是否启用供应。
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 分组后的供应记忆。
    /// </summary>
    public IReadOnlyList<MemorySupplyGroup> Groups { get; init; } = [];

    /// <summary>
    /// 扁平化供应记忆列表。
    /// </summary>
    public IReadOnlyList<MemorySupplyItem> Items { get; init; } = [];

    /// <summary>
    /// 整体可信度。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 供应摘要。
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// 本次供应预算使用情况。
    /// </summary>
    public MemorySupplyBudget Budget { get; init; } = new();

    /// <summary>
    /// 本次应用的供应策略。
    /// </summary>
    public MemorySupplyPolicy AppliedPolicy { get; init; } = new();

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// 表示一组主动供应记忆。
/// </summary>
public sealed class MemorySupplyGroup
{
    /// <summary>
    /// 分组键。
    /// </summary>
    public string GroupKey { get; init; } = string.Empty;

    /// <summary>
    /// 分组标题。
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 分组内记忆项。
    /// </summary>
    public IReadOnlyList<MemorySupplyItem> Items { get; init; } = [];

    /// <summary>
    /// 分组优先级，数值越小优先级越高。
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// 表示单条主动供应记忆。
/// </summary>
public sealed class MemorySupplyItem
{
    /// <summary>
    /// 记忆标识。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 记忆类型。
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// 主题。
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// 标题。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 可供上层注入的内容。
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 供应原因。
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 可信度。
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// 供应排序分。
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// 来源召回分。
    /// </summary>
    public double SourceRecallScore { get; init; }

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

/// <summary>
/// 表示主动记忆供应预算。
/// </summary>
public sealed class MemorySupplyBudget
{
    /// <summary>
    /// 最大记忆数量。
    /// </summary>
    public int MaxMemoryCount { get; init; }

    /// <summary>
    /// 实际使用记忆数量。
    /// </summary>
    public int UsedMemoryCount { get; init; }

    /// <summary>
    /// 最大 Token 预算。
    /// </summary>
    public int? MaxTokenBudget { get; init; }

    /// <summary>
    /// 估算 Token 数量。
    /// </summary>
    public int? EstimatedTokens { get; init; }
}

/// <summary>
/// 表示主动记忆供应策略。
/// </summary>
public sealed class MemorySupplyPolicy
{
    /// <summary>
    /// 是否启用供应。
    /// </summary>
    public bool SupplyEnabled { get; init; }

    /// <summary>
    /// 最大记忆数量。
    /// </summary>
    public int MaxMemoryCount { get; init; }

    /// <summary>
    /// 召回最低可信度。
    /// </summary>
    public double RecallMinimumConfidence { get; init; }

    /// <summary>
    /// 排序策略说明。
    /// </summary>
    public string Ranking { get; init; } = string.Empty;

    /// <summary>
    /// 分组策略说明。
    /// </summary>
    public string Grouping { get; init; } = string.Empty;
}
