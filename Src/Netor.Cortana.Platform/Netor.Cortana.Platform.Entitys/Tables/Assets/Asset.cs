using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;

namespace Netor.Cortana.Platform.Entitys.Tables.Assets;

/// <summary>
/// 平台资源：插件、技能、智能体和解决方案。
/// </summary>
[Comment("平台资源")]
public sealed class Asset : Base
{
    [StringLength(128)]
    [Comment("名称")]
    [Display(Name = "名称")]
    public string Name { get; set; } = string.Empty;

    [StringLength(160)]
    [Comment("标识")]
    [Display(Name = "标识")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(128)]
    [Comment("开发者")]
    [Display(Name = "开发者")]
    public string DeveloperName { get; set; } = string.Empty;

    [StringLength(32)]
    [Comment("归属账号ID")]
    [Display(Name = "归属账号ID")]
    public string? OwnerAccountId { get; set; }

    public Account? OwnerAccount { get; set; }

    [StringLength(256)]
    [Comment("简短描述")]
    [Display(Name = "简短描述")]
    public string ShortDescription { get; set; } = string.Empty;

    [Comment("详细描述")]
    [Display(Name = "详细描述")]
    public string Description { get; set; } = string.Empty;

    [StringLength(512)]
    [Comment("标签")]
    [Display(Name = "标签")]
    public string Tags { get; set; } = string.Empty;

    [StringLength(512)]
    [Comment("图标地址")]
    [Display(Name = "图标地址")]
    public string? IconUrl { get; set; }

    [StringLength(512)]
    [Comment("封面地址")]
    [Display(Name = "封面地址")]
    public string? CoverUrl { get; set; }

    [Comment("类型")]
    [Display(Name = "类型")]
    public AssetType Type { get; set; }

    [Comment("状态")]
    [Display(Name = "状态")]
    public AssetStatus Status { get; set; } = AssetStatus.Draft;

    [Comment("分类ID")]
    [Display(Name = "分类ID")]
    public string? CategoryId { get; set; }

    public Category? Category { get; set; }

    [Comment("当前版本ID")]
    [Display(Name = "当前版本ID")]
    public string? CurrentVersionId { get; set; }

    [Comment("是否推荐")]
    [Display(Name = "是否推荐")]
    public bool IsFeatured { get; set; }

    [Comment("下载次数")]
    [Display(Name = "下载次数")]
    public int DownloadCount { get; set; }

    [Comment("发布时间")]
    [Display(Name = "发布时间")]
    public DateTimeOffset? PublishedAtUtc { get; set; }

    public ICollection<AssetVersion> Versions { get; set; } = [];

    public ICollection<PricingPlan> PricingPlans { get; set; } = [];
}