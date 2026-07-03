namespace Netor.Cortana.Platform.Entitys.Tables.Assets;

/// <summary>
/// 平台资源分类。
/// </summary>
[Comment("平台资源分类")]
public sealed class Category : Base
{
    [StringLength(64)]
    [Comment("名称")]
    [Display(Name = "名称")]
    public string Name { get; set; } = string.Empty;

    [StringLength(128)]
    [Comment("标识")]
    [Display(Name = "标识")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(256)]
    [Comment("描述")]
    [Display(Name = "描述")]
    public string? Description { get; set; }

    [Comment("排序")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }

    [Comment("是否显示")]
    [Display(Name = "是否显示")]
    public bool IsVisible { get; set; } = true;

    public ICollection<Asset> Assets { get; set; } = [];
}