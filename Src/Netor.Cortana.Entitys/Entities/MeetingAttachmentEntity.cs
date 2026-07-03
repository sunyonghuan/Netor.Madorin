namespace Netor.Cortana.Entitys;

/// <summary>
/// 会议模式附件实体（MeetingAttachments 表）。
/// </summary>
public sealed class MeetingAttachmentEntity
{
    /// <summary>附件 ID（GUID(N)）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联会议 ID。</summary>
    public string MeetingId { get; set; } = string.Empty;

    /// <summary>原始文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>存储路径，相对 workspace resources 目录。</summary>
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>MIME 类型。</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>文件大小。</summary>
    public long SizeBytes { get; set; }

    /// <summary>上传时间（Unix 毫秒）。</summary>
    public long UploadedAt { get; set; }
}
