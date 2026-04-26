namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示记忆系统内部处理过程中的事件记录。
/// </summary>
public sealed class MemoryEvent
{
    /// <summary>
    /// 记忆事件唯一标识。
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// 事件所属智能体标识。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 事件类型，例如 extracted、reinforced、decayed 或 forgotten。
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// 事件负载的 JSON 表示。
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// 事件被处理完成的 ISO 8601 UTC 时间。
    /// </summary>
    public string? ProcessedAt { get; set; }

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;
}
