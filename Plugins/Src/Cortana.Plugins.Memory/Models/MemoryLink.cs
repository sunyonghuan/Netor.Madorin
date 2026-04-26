namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示记忆片段或抽象记忆之间的关系边。
/// </summary>
public sealed class MemoryLink
{
    /// <summary>
    /// 记忆关系唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 记忆关系所属智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 源记忆标识。
    /// </summary>
    public string SourceMemoryId { get; set; } = string.Empty;

    /// <summary>
    /// 源记忆类型，例如 fragment 或 abstraction。
    /// </summary>
    public string SourceMemoryKind { get; set; } = string.Empty;

    /// <summary>
    /// 目标记忆标识。
    /// </summary>
    public string TargetMemoryId { get; set; } = string.Empty;

    /// <summary>
    /// 目标记忆类型，例如 fragment 或 abstraction。
    /// </summary>
    public string TargetMemoryKind { get; set; } = string.Empty;

    /// <summary>
    /// 关系类型，例如 supports、contradicts、related 或 derived_from。
    /// </summary>
    public string RelationType { get; set; } = string.Empty;

    /// <summary>
    /// 关系权重，用于图检索和排序。
    /// </summary>
    public double Weight { get; set; }

    /// <summary>
    /// 支撑该关系的证据数量。
    /// </summary>
    public int EvidenceCount { get; set; }

    /// <summary>
    /// 关系可信度评分。
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 关系创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// 关系最后更新时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
