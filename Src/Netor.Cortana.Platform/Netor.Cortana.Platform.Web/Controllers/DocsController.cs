using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;
using Markdig;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Web.Models.Docs;

namespace Netor.Cortana.Platform.Web.Controllers;

[Route("docs")]
public sealed class DocsController(PlatformDbContext dbContext) : Controller
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var categories = await dbContext.DocCategories
            .AsNoTracking()
            .Where(x => x.IsVisible)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new DocsCategorySummary(
                x.Name,
                x.Slug,
                x.Description ?? string.Empty,
                x.Articles.Count(a => a.Status == DocArticleStatus.Published)))
            .ToListAsync(cancellationToken);

        var articles = await dbContext.DocArticles
            .AsNoTracking()
            .Include(x => x.Category)
            .Where(x => x.Status == DocArticleStatus.Published && x.Category != null && x.Category.IsVisible)
            .OrderBy(x => x.Category!.SortOrder)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Title)
            .Select(x => new DocsArticleSummary(
                x.Title,
                x.Slug,
                x.Summary,
                x.Category!.Name,
                x.Category.Slug,
                x.PublishedAtUtc))
            .ToListAsync(cancellationToken);

        return View(new DocsIndexViewModel
        {
            Categories = categories,
            Articles = articles
        });
    }

    [HttpGet("{categorySlug}")]
    public async Task<IActionResult> Category(string categorySlug, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories
            .AsNoTracking()
            .Include(x => x.Articles)
            .FirstOrDefaultAsync(x => x.Slug == categorySlug && x.IsVisible, cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        return View(new DocsCategoryViewModel
        {
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description ?? string.Empty,
            Articles = category.Articles
                .Where(x => x.Status == DocArticleStatus.Published)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Title)
                .Select(x => new DocsArticleSummary(
                    x.Title,
                    x.Slug,
                    x.Summary,
                    category.Name,
                    category.Slug,
                    x.PublishedAtUtc))
                .ToList()
        });
    }

    [HttpGet("{categorySlug}/{articleSlug}")]
    public async Task<IActionResult> Article(string categorySlug, string articleSlug, CancellationToken cancellationToken)
    {
        var article = await dbContext.DocArticles
            .AsNoTracking()
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x =>
                x.Slug == articleSlug &&
                x.Status == DocArticleStatus.Published &&
                x.Category != null &&
                x.Category.Slug == categorySlug &&
                x.Category.IsVisible,
                cancellationToken);
        if (article?.Category is null)
        {
            return NotFound();
        }

        return View(new DocsArticleViewModel
        {
            CategoryName = article.Category.Name,
            CategorySlug = article.Category.Slug,
            Title = article.Title,
            Slug = article.Slug,
            Summary = article.Summary,
            ContentMarkdown = article.ContentMarkdown,
            ContentHtml = new HtmlString(Markdown.ToHtml(article.ContentMarkdown, MarkdownPipeline)),
            PublishedAtUtc = article.PublishedAtUtc
        });
    }
}
