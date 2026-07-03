using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Entitys.Tables.Docs;

/// <summary>
/// 官网文档文章。
/// </summary>
[Comment("官网文档文章")]
public sealed class DocArticle : Base
{
    [StringLength(32)]
    [Comment("分类ID")]
    [Display(Name = "分类ID")]
    public string CategoryId { get; set; } = string.Empty;

    public DocCategory? Category { get; set; }

    [StringLength(128)]
    [Comment("标题")]
    [Display(Name = "标题")]
    public string Title { get; set; } = string.Empty;

    [StringLength(160)]
    [Comment("标识")]
    [Display(Name = "标识")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(256)]
    [Comment("摘要")]
    [Display(Name = "摘要")]
    public string Summary { get; set; } = string.Empty;

    [Comment("正文")]
    [Display(Name = "正文")]
    public string ContentMarkdown { get; set; } = string.Empty;

    [StringLength(512)]
    [Comment("搜索关键词")]
    [Display(Name = "搜索关键词")]
    public string Keywords { get; set; } = string.Empty;

    [Comment("状态")]
    [Display(Name = "状态")]
    public DocArticleStatus Status { get; set; } = DocArticleStatus.Draft;

    [Comment("排序")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }

    [Comment("发布时间")]
    [Display(Name = "发布时间")]
    public DateTimeOffset? PublishedAtUtc { get; set; }
}
