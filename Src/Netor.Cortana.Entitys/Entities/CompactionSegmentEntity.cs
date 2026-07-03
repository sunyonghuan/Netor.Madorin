namespace Netor.Cortana.Entitys;

/// <summary>
/// 会话压缩段落实体。每个段落是一段原始消息经 LLM 单次摘要后的不可变快照。
/// 段落一旦创建永不修改，避免二次压缩导致信息递归丢失。
/// </summary>
public class CompactionSegmentEntity : BaseEntity
{
    /// <summary>所属会话 ID。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>段落序号（从 0 开始递增），用于排序。</summary>
    public int SegmentIndex { get; set; }

    /// <summary>该段落覆盖的起始消息索引（含），基于 CreatedTimestamp 正序。</summary>
    public int StartMessageIndex { get; set; }

    /// <summary>该段落覆盖的结束消息索引（含），基于 CreatedTimestamp 正序。</summary>
    public int EndMessageIndex { get; set; }

    /// <summary>LLM 生成的摘要文本。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>该段落涵盖的原始消息条数。</summary>
    public int OriginalMessageCount { get; set; }

    /// <summary>生成摘要时使用的模型名称。</summary>
    public string ModelName { get; set; } = string.Empty;
}
