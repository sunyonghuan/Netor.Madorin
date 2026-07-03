namespace Netor.Cortana.Platform.Entitys.Tables.Assets;

/// <summary>
/// 平台资源版本。
/// </summary>
[Comment("平台资源版本")]
public sealed class AssetVersion : Base
{
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }

    [StringLength(32)]
    [Comment("版本号")]
    [Display(Name = "版本号")]
    public string VersionName { get; set; } = "1.0.0";

    [Comment("发布说明")]
    [Display(Name = "发布说明")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [Comment("清单JSON")]
    [Display(Name = "清单JSON")]
    public string ManifestJson { get; set; } = string.Empty;

    [StringLength(128)]
    [Comment("包哈希")]
    [Display(Name = "包哈希")]
    public string PackageHash { get; set; } = string.Empty;

    [StringLength(32)]
    [Comment("存储提供程序")]
    [Display(Name = "存储提供程序")]
    public string StorageProvider { get; set; } = "Local";

    [Comment("包大小")]
    [Display(Name = "包大小")]
    public long PackageSize { get; set; }

    [StringLength(1024)]
    [Comment("文件路径")]
    [Display(Name = "文件路径")]
    public string FilePath { get; set; } = string.Empty;
}
