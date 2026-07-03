namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作模式 Run 级上下文压缩段。
/// 当前阶段先落表与服务，为后续真正压缩保留结构。
/// </summary>
public sealed class WorkTaskContextSegmentEntity
{
    /// <summary>压缩段 ID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的 WorkTasks.Id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>AF RunId，A/C 各自独立。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Run 内段落序号。</summary>
    public int SegmentIndex { get; set; }

    /// <summary>覆盖的起始消息序号。</summary>
    public long StartSequence { get; set; }

    /// <summary>覆盖的结束消息序号。</summary>
    public long EndSequence { get; set; }

    /// <summary>摘要文本。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>原始消息条数。</summary>
    public int OriginalMessageCount { get; set; }

    /// <summary>生成摘要时使用的模型名称。</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }
}
