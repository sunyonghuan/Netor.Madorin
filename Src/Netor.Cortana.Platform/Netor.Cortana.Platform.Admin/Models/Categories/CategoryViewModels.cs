using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Admin.Models.Categories;

public sealed record CategoryListItem(
    string Id,
    string Name,
    string Slug,
    string? Description,
    int SortOrder,
    bool IsVisible,
    int AssetCount);

public sealed record CategoryTableItem(
    string Id,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsVisible,
    string VisibleName,
    string VisibleBadgeClass,
    int AssetCount,
    int PublishedAssetCount,
    long TimeStamp);

public sealed record CategoryAssetItem(
    string Id,
    string Name,
    string Slug,
    string TypeName,
    string StatusName,
    int DownloadCount);

public sealed class CategoryIndexViewModel
{
    public IReadOnlyList<CategoryListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public bool? Visible { get; init; }

    public int TotalCount { get; init; }

    public int VisibleCount { get; init; }

    public int HiddenCount { get; init; }
}

public sealed class CategoryEditViewModel
{
    public string? Id { get; init; }

    [Required(ErrorMessage = "请输入分类名称")]
    [StringLength(64, ErrorMessage = "分类名称不能超过 64 个字符")]
    [Display(Name = "分类名称")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入分类标识")]
    [StringLength(128, ErrorMessage = "分类标识不能超过 128 个字符")]
    [RegularExpression("^[a-z0-9][a-z0-9-]*$", ErrorMessage = "分类标识只能使用小写字母、数字和中横线")]
    [Display(Name = "分类标识")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(256, ErrorMessage = "描述不能超过 256 个字符")]
    [Display(Name = "分类描述")]
    public string? Description { get; set; }

    [Display(Name = "排序")]
    public int SortOrder { get; set; }

    [Display(Name = "是否显示")]
    public bool IsVisible { get; set; } = true;
}

public sealed class CategoryDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public bool IsVisible { get; init; }

    public int AssetCount { get; init; }

    public int PublishedAssetCount { get; init; }

    public IReadOnlyList<CategoryAssetItem> Assets { get; init; } = [];
}
