using System.ComponentModel.DataAnnotations;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Models.Docs;

public sealed record DocCategoryOption(string Id, string Name);

public sealed record DocArticleListItem(
    string Id,
    string Title,
    string Slug,
    string Summary,
    string CategoryName,
    DocArticleStatus Status,
    string StatusName,
    int SortOrder,
    DateTimeOffset? PublishedAtUtc);

public sealed record DocArticleTableItem(
    string Id,
    string Title,
    string Slug,
    string Summary,
    string CategoryName,
    DocArticleStatus Status,
    string StatusName,
    string StatusBadgeClass,
    int SortOrder,
    string Keywords,
    int ContentLength,
    string PublishedAtText,
    long TimeStamp);

public sealed record DocCategoryListItem(
    string Id,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsVisible,
    int ArticleCount,
    int PublishedArticleCount);

public sealed record DocCategoryTableItem(
    string Id,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsVisible,
    string VisibleName,
    string VisibleBadgeClass,
    int ArticleCount,
    int PublishedArticleCount,
    long TimeStamp);

public sealed class DocIndexViewModel
{
    public IReadOnlyList<DocArticleListItem> Items { get; init; } = [];

    public IReadOnlyList<DocCategoryOption> Categories { get; init; } = [];

    public string? Keyword { get; init; }

    public string? CategoryId { get; init; }

    public DocArticleStatus? Status { get; init; }

    public int TotalCount { get; init; }

    public int PublishedCount { get; init; }

    public int DraftCount { get; init; }
}

public sealed class DocCategoriesViewModel
{
    public IReadOnlyList<DocCategoryListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public bool? Visible { get; init; }

    public int TotalCount { get; init; }

    public int VisibleCount { get; init; }
}

public sealed class DocArticleEditViewModel
{
    public string? Id { get; init; }

    [Required(ErrorMessage = "请选择文档分类")]
    [Display(Name = "文档分类")]
    public string CategoryId { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入文章标题")]
    [StringLength(128, ErrorMessage = "文章标题不能超过 128 个字符")]
    [Display(Name = "文章标题")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入文章标识")]
    [StringLength(160, ErrorMessage = "文章标识不能超过 160 个字符")]
    [RegularExpression("^[a-z0-9][a-z0-9-]*$", ErrorMessage = "文章标识只能使用小写字母、数字和中横线")]
    [Display(Name = "文章标识")]
    public string Slug { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入文章摘要")]
    [StringLength(256, ErrorMessage = "文章摘要不能超过 256 个字符")]
    [Display(Name = "文章摘要")]
    public string Summary { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入文章正文")]
    [Display(Name = "文章正文")]
    public string ContentMarkdown { get; set; } = string.Empty;

    [StringLength(512, ErrorMessage = "搜索关键词不能超过 512 个字符")]
    [Display(Name = "搜索关键词")]
    public string? Keywords { get; set; }

    [Display(Name = "状态")]
    public DocArticleStatus Status { get; set; } = DocArticleStatus.Draft;

    [Display(Name = "排序")]
    public int SortOrder { get; set; } = 50;

    public IReadOnlyList<DocCategoryOption> Categories { get; set; } = [];

    public bool IsEdit => !string.IsNullOrWhiteSpace(Id);
}

public sealed class DocCategoryEditViewModel
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

    [StringLength(256, ErrorMessage = "分类描述不能超过 256 个字符")]
    [Display(Name = "分类描述")]
    public string? Description { get; set; }

    [Display(Name = "排序")]
    public int SortOrder { get; set; } = 50;

    [Display(Name = "是否显示")]
    public bool IsVisible { get; set; } = true;

    public bool IsEdit => !string.IsNullOrWhiteSpace(Id);
}
