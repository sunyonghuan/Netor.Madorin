using Microsoft.AspNetCore.Html;

namespace Netor.Cortana.Platform.Web.Models.Docs;

public sealed record DocsCategorySummary(
    string Name,
    string Slug,
    string Description,
    int ArticleCount);

public sealed record DocsArticleSummary(
    string Title,
    string Slug,
    string Summary,
    string CategoryName,
    string CategorySlug,
    DateTimeOffset? PublishedAtUtc);

public sealed class DocsIndexViewModel
{
    public IReadOnlyList<DocsCategorySummary> Categories { get; init; } = [];

    public IReadOnlyList<DocsArticleSummary> Articles { get; init; } = [];
}

public sealed class DocsCategoryViewModel
{
    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<DocsArticleSummary> Articles { get; init; } = [];
}

public sealed class DocsArticleViewModel
{
    public string CategoryName { get; init; } = string.Empty;

    public string CategorySlug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string ContentMarkdown { get; init; } = string.Empty;

    public IHtmlContent ContentHtml { get; init; } = HtmlString.Empty;

    public DateTimeOffset? PublishedAtUtc { get; init; }
}
