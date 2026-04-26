using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 记忆处理请求。
/// </summary>
public sealed class MemoryProcessingRequest
{
    /// <summary>
    /// 本次处理请求标识。
    /// </summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 待处理的智能体标识，为空时处理全部智能体。
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// 待处理的工作区标识，为空时处理全部工作区。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 本次最多处理的观察记录数量。
    /// </summary>
    public int MaxObservationCount { get; set; } = 100;

    /// <summary>
    /// 触发来源，例如 manual、hosted-service 或 ingest。
    /// </summary>
    public string TriggerSource { get; set; } = "manual";

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; set; }
}

/// <summary>
/// 记忆处理结果。
/// </summary>
public sealed class MemoryProcessingResult
{
    /// <summary>
    /// 本次处理请求标识。
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// 已处理观察记录数量。
    /// </summary>
    public int ProcessedObservationCount { get; set; }

    /// <summary>
    /// 创建长期记忆片段数量。
    /// </summary>
    public int CreatedFragmentCount { get; set; }

    /// <summary>
    /// 合并长期记忆片段数量。
    /// </summary>
    public int MergedFragmentCount { get; set; }

    /// <summary>
    /// 创建抽象记忆数量。
    /// </summary>
    public int CreatedAbstractionCount { get; set; }

    /// <summary>
    /// 处理失败的观察记录数量。
    /// </summary>
    public int FailedObservationCount { get; set; }

    /// <summary>
    /// 本次处理结束状态。
    /// </summary>
    public string State { get; set; } = "completed";

    /// <summary>
    /// 处理摘要。
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// 记忆处理器的持久化状态。
/// </summary>
public sealed class MemoryProcessingState
{
    /// <summary>
    /// 状态记录标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 处理器名称。
    /// </summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>
    /// 智能体标识。
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// 工作区标识。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 当前状态，例如 idle、running、completed 或 failed。
    /// </summary>
    public string State { get; set; } = "idle";

    /// <summary>
    /// 已处理到的观察记录时间戳。
    /// </summary>
    public long LastObservationTimestamp { get; set; }

    /// <summary>
    /// 已处理到的观察记录标识。
    /// </summary>
    public string? LastObservationId { get; set; }

    /// <summary>
    /// 累计处理数量。
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// 累计创建片段数量。
    /// </summary>
    public int CreatedFragmentCount { get; set; }

    /// <summary>
    /// 累计合并片段数量。
    /// </summary>
    public int MergedFragmentCount { get; set; }

    /// <summary>
    /// 累计创建抽象数量。
    /// </summary>
    public int CreatedAbstractionCount { get; set; }

    /// <summary>
    /// 最近一次错误。
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// 处理锁过期时间。
    /// </summary>
    public string? LockedUntil { get; set; }

    /// <summary>
    /// 状态创建时间。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// 状态更新时间。
    /// </summary>
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>
/// 对单条观察记录进行语义分析后的候选记忆。
/// </summary>
public sealed class MemorySemanticCandidate
{
    /// <summary>
    /// 记忆类型。
    /// </summary>
    public string MemoryType { get; set; } = "fact";

    /// <summary>
    /// 记忆主题。
    /// </summary>
    public string Topic { get; set; } = "general";

    /// <summary>
    /// 记忆标题。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 记忆摘要。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 记忆详情。
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// 关键词集合。
    /// </summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>
    /// 重要性评分。
    /// </summary>
    public double Importance { get; set; } = 0.65;

    /// <summary>
    /// 可信度评分。
    /// </summary>
    public double Confidence { get; set; } = 0.6;

    /// <summary>
    /// 新颖度评分。
    /// </summary>
    public double Novelty { get; set; } = 0.6;

    /// <summary>
    /// 生成该候选记忆的原始观察记录。
    /// </summary>
    public ObservationRecord SourceObservation { get; set; } = new();
}
