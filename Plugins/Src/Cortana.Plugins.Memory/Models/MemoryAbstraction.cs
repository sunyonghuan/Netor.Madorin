namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示由多个记忆片段归纳形成的抽象记忆。
/// </summary>
public sealed class MemoryAbstraction
{
    /// <summary>
    /// 抽象记忆唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 抽象记忆所属智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 抽象记忆所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 抽象类型，例如偏好归纳、长期事实、行为模式或项目约束。
    /// </summary>
    public string AbstractionType { get; set; } = string.Empty;

    /// <summary>
    /// 抽象记忆标题。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 抽象记忆的核心陈述。
    /// </summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>
    /// 抽象记忆摘要。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 支撑该抽象记忆的记忆标识集合 JSON。
    /// </summary>
    public string SupportingMemoryIdsJson { get; set; } = "[]";

    /// <summary>
    /// 与该抽象记忆相冲突的记忆标识集合 JSON。
    /// </summary>
    public string? CounterMemoryIdsJson { get; set; }

    /// <summary>
    /// 关键词集合的 JSON 表示。
    /// </summary>
    public string? KeywordsJson { get; set; }

    /// <summary>
    /// 标签集合的 JSON 表示。
    /// </summary>
    public string? TagsJson { get; set; }

    /// <summary>
    /// 抽象记忆重要性评分。
    /// </summary>
    public double Importance { get; set; }

    /// <summary>
    /// 抽象记忆可信度评分。
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// 稳定性评分，用于衡量抽象结论是否长期成立。
    /// </summary>
    public double StabilityScore { get; set; }

    /// <summary>
    /// 保留评分，用于决定抽象记忆保留和遗忘策略。
    /// </summary>
    public double RetentionScore { get; set; }

    /// <summary>
    /// 衰减率，用于后续记忆老化计算。
    /// </summary>
    public double DecayRate { get; set; }

    /// <summary>
    /// 抽象记忆被访问的次数。
    /// </summary>
    public int AccessCount { get; set; }

    /// <summary>
    /// 抽象记忆被强化的次数。
    /// </summary>
    public int ReinforcementCount { get; set; }

    /// <summary>
    /// 抽象记忆被矛盾证据命中的次数。
    /// </summary>
    public int ContradictionCount { get; set; }

    /// <summary>
    /// 清晰度等级，例如 blurred、clear 或 precise。
    /// </summary>
    public string ClarityLevel { get; set; } = "blurred";

    /// <summary>
    /// 确认状态，例如 pending、confirmed 或 rejected。
    /// </summary>
    public string ConfirmationState { get; set; } = "pending";

    /// <summary>
    /// 生命周期状态，例如 candidate、active、archived 或 forgotten。
    /// </summary>
    public string LifecycleState { get; set; } = "candidate";

    /// <summary>
    /// 最近一次校验该抽象记忆的 ISO 8601 UTC 时间。
    /// </summary>
    public string? LastValidatedAt { get; set; }

    /// <summary>
    /// 最近一次访问该抽象记忆的 ISO 8601 UTC 时间。
    /// </summary>
    public string? LastAccessedAt { get; set; }

    /// <summary>
    /// 抽象记忆过期时间。
    /// </summary>
    public string? ExpiresAt { get; set; }

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 兼容性标签集合 JSON，用于迁移和策略适配。
    /// </summary>
    public string? CompatibilityTagsJson { get; set; }

    /// <summary>
    /// 抽象记忆创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// 抽象记忆最后更新时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
