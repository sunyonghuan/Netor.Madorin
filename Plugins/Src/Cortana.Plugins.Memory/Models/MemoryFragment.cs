namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示从观察记录中提取出的长期记忆片段。
/// </summary>
public sealed class MemoryFragment
{
    /// <summary>
    /// 记忆片段唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 记忆片段所属智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 记忆片段所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 记忆类型，例如事实、偏好、任务或约束。
    /// </summary>
    public string MemoryType { get; set; } = string.Empty;

    /// <summary>
    /// 记忆主题，用于聚类、检索和召回排序。
    /// </summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// 记忆片段标题。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 记忆片段摘要。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 记忆片段详细内容。
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// 关键词集合的 JSON 表示。
    /// </summary>
    public string? KeywordsJson { get; set; }

    /// <summary>
    /// 标签集合的 JSON 表示。
    /// </summary>
    public string? TagsJson { get; set; }

    /// <summary>
    /// 实体识别结果的 JSON 表示。
    /// </summary>
    public string? EntitiesJson { get; set; }

    /// <summary>
    /// 支撑该记忆片段的观察记录标识集合 JSON。
    /// </summary>
    public string SourceObservationIdsJson { get; set; } = "[]";

    /// <summary>
    /// 来源会话标识集合 JSON。
    /// </summary>
    public string? SourceSessionIdsJson { get; set; }

    /// <summary>
    /// 来源轮次标识集合 JSON。
    /// </summary>
    public string? SourceTurnIdsJson { get; set; }

    /// <summary>
    /// 记忆重要性评分。
    /// </summary>
    public double Importance { get; set; }

    /// <summary>
    /// 记忆可信度评分。
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// 情绪权重，用于衡量该记忆的情绪影响。
    /// </summary>
    public double EmotionalWeight { get; set; }

    /// <summary>
    /// 新颖度评分，用于区分新信息与重复信息。
    /// </summary>
    public double Novelty { get; set; }

    /// <summary>
    /// 显著性评分，用于候选记忆排序。
    /// </summary>
    public double SalienceScore { get; set; }

    /// <summary>
    /// 保留评分，用于决定记忆保留和遗忘策略。
    /// </summary>
    public double RetentionScore { get; set; }

    /// <summary>
    /// 衰减率，用于后续记忆老化计算。
    /// </summary>
    public double DecayRate { get; set; }

    /// <summary>
    /// 记忆被召回或访问的次数。
    /// </summary>
    public int AccessCount { get; set; }

    /// <summary>
    /// 记忆被强化的次数。
    /// </summary>
    public int ReinforcementCount { get; set; }

    /// <summary>
    /// 记忆被矛盾证据命中的次数。
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
    /// 最近一次访问该记忆的 ISO 8601 UTC 时间。
    /// </summary>
    public string? LastAccessedAt { get; set; }

    /// <summary>
    /// 最近一次强化该记忆的 ISO 8601 UTC 时间。
    /// </summary>
    public string? LastReinforcedAt { get; set; }

    /// <summary>
    /// 记忆过期时间。
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
    /// 记忆创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// 记忆最后更新时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
