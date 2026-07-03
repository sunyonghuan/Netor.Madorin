namespace Netor.Cortana.Entitys;

/// <summary>
/// 会议模式上下文压缩段实体（MeetingCompactionSegments 表）。
/// </summary>
public sealed class MeetingCompactionSegmentEntity
{
    /// <summary>压缩段 ID（GUID(N)）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联会议 ID。</summary>
    public string MeetingId { get; set; } = string.Empty;

    /// <summary>段落序号。</summary>
    public int SegmentIndex { get; set; }

    /// <summary>覆盖的起始消息序号。</summary>
    public int StartSequence { get; set; }

    /// <summary>覆盖的结束消息序号。</summary>
    public int EndSequence { get; set; }

    /// <summary>段落摘要。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>原始消息数量。</summary>
    public int OriginalMessageCount { get; set; }

    /// <summary>生成摘要的模型名。</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }
}
