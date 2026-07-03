namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作模式 Run 级上下文消息。
/// A/C 各自用独立 RunId 保存上下文，B 层不写入此表。
/// </summary>
public sealed class WorkTaskContextMessageEntity
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>关联的 WorkTasks.Id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>AF RunId，A/C 各自独立。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Run 内消息序号。</summary>
    public long Sequence { get; set; }

    /// <summary>消息角色：system/user/assistant/tool。</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>纯文本内容快照。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
