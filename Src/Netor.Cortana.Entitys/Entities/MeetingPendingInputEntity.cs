namespace Netor.Cortana.Entitys;

/// <summary>
/// 会议模式用户插话队列实体（MeetingPendingInputs 表）。
/// </summary>
public sealed class MeetingPendingInputEntity
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>关联会议 ID。</summary>
    public string MeetingId { get; set; } = string.Empty;

    /// <summary>用户输入内容。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>附件引用 JSON。</summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>输入类型：interrupt / reply。</summary>
    public string Kind { get; set; } = "interrupt";

    /// <summary>入队时间（Unix 毫秒）。</summary>
    public long EnqueuedAt { get; set; }

    /// <summary>是否已消费。</summary>
    public bool Consumed { get; set; }

    /// <summary>消费时间（Unix 毫秒）。</summary>
    public long? ConsumedAt { get; set; }
}
