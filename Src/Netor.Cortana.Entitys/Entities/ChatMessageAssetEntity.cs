namespace Netor.Cortana.Entitys;

/// <summary>
/// 聊天消息关联的资源（图片、音频、视频、文件）索引实体。
/// 存储在 ChatMessageAssets 表中，通过 SessionId + MessageId 与聊天消息关联。
/// </summary>
public class ChatMessageAssetEntity : BaseEntity
{
    /// <summary>所属会话 ID。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>所属消息 ID。</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>消息角色：user / assistant。</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>资源分组：images / audio / video / files。</summary>
    public string AssetGroup { get; set; } = string.Empty;

    /// <summary>资源种类：attachment / generated / inline。</summary>
    public string AssetKind { get; set; } = string.Empty;

    /// <summary>MIME 类型，如 image/png、audio/wav。</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>原始文件名。</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>相对于 .cortana/resources/ 的存储路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>文件 SHA256 哈希。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>同一消息内的排序序号。</summary>
    public int SortOrder { get; set; }

    /// <summary>图片宽度（像素），非图片为 0。</summary>
    public int Width { get; set; }

    /// <summary>图片高度（像素），非图片为 0。</summary>
    public int Height { get; set; }

    /// <summary>音视频时长（毫秒），非音视频为 0。</summary>
    public long DurationMs { get; set; }

    /// <summary>来源类型：local / download / generated。</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>状态：active / deleted / orphaned。</summary>
    public string Status { get; set; } = "active";
}
