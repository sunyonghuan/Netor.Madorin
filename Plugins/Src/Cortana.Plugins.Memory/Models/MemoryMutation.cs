namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示一次记忆内容、状态或评分变更的审计记录。
/// </summary>
public sealed class MemoryMutation
{
    /// <summary>
    /// 记忆变更记录唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 变更所属智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 被变更的记忆标识。
    /// </summary>
    public string MemoryId { get; set; } = string.Empty;

    /// <summary>
    /// 被变更的记忆类型，例如 fragment、abstraction 或 link。
    /// </summary>
    public string MemoryKind { get; set; } = string.Empty;

    /// <summary>
    /// 变更类型，例如 create、update、merge、decay、forget 或 restore。
    /// </summary>
    public string MutationType { get; set; } = string.Empty;

    /// <summary>
    /// 变更前状态的 JSON 表示。
    /// </summary>
    public string? BeforeJson { get; set; }

    /// <summary>
    /// 变更后状态的 JSON 表示。
    /// </summary>
    public string? AfterJson { get; set; }

    /// <summary>
    /// 变更原因说明。
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 变更记录创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
