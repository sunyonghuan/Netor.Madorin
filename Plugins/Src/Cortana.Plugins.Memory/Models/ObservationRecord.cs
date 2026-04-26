namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示从宿主会话事实流接收到的一条原始观察记录。
/// </summary>
public sealed class ObservationRecord
{
    /// <summary>
    /// 观察记录唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 产生该观察记录的智能体标识。
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// 观察记录所属工作区标识。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 观察记录所属会话标识。
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 会话轮次标识。
    /// </summary>
    public string? TurnId { get; set; }

    /// <summary>
    /// 来源消息标识。
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// 事实流事件类型。
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// 消息角色，例如用户、助手或系统。
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// 观察记录的文本内容。
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// 附件信息的 JSON 表示。
    /// </summary>
    public string AttachmentsJson { get; set; } = "[]";

    /// <summary>
    /// 来源事件创建时间戳，通常为 Unix 毫秒。
    /// </summary>
    public long CreatedTimestamp { get; set; }

    /// <summary>
    /// 生成该消息或事件时使用的模型名称。
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// 链路追踪标识。
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 原始来源事实的 JSON 快照。
    /// </summary>
    public string SourceFactsJson { get; set; } = "{}";

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 记录写入记忆库的 ISO 8601 UTC 时间。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
