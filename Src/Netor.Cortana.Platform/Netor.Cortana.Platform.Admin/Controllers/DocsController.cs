using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Docs;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Docs;
using Netor.Cortana.Platform.Services.Docs;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class DocsController(PlatformDbContext dbContext, DocsMediaPathService mediaPathService) : Controller
{
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<IActionResult> Index(string? keyword, string? categoryId, DocArticleStatus? status, CancellationToken cancellationToken)
    {
        var model = new DocIndexViewModel
        {
            Items = [],
            Categories = await GetCategoryOptionsAsync(false, cancellationToken),
            Keyword = keyword,
            CategoryId = categoryId,
            Status = status,
            TotalCount = await dbContext.DocArticles.CountAsync(cancellationToken),
            PublishedCount = await dbContext.DocArticles.CountAsync(x => x.Status == DocArticleStatus.Published, cancellationToken),
            DraftCount = await dbContext.DocArticles.CountAsync(x => x.Status == DocArticleStatus.Draft, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        string? categoryId,
        DocArticleStatus? status,
        int? sortMin,
        int? sortMax,
        int? contentMin,
        int? contentMax,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.DocArticles
            .AsNoTracking()
            .Include(x => x.Category)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Title.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                x.Summary.Contains(normalizedKeyword) ||
                x.Keywords.Contains(normalizedKeyword) ||
                x.ContentMarkdown.Contains(normalizedKeyword));
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (sortMin is not null)
        {
            query = query.Where(x => x.SortOrder >= sortMin);
        }

        if (sortMax is not null)
        {
            query = query.Where(x => x.SortOrder <= sortMax);
        }

        if (contentMin is not null)
        {
            query = query.Where(x => x.ContentMarkdown.Length >= contentMin);
        }

        if (contentMax is not null)
        {
            query = query.Where(x => x.ContentMarkdown.Length <= contentMax);
        }

        var count = await query.CountAsync(cancellationToken);
        var articles = await ApplyArticleSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = articles.Select(x => new DocArticleTableItem(
                x.ID,
                x.Title,
                x.Slug,
                x.Summary,
                x.Category == null ? "-" : x.Category.Name,
                x.Status,
                GetStatusName(x.Status),
                GetStatusBadgeClass(x.Status),
                x.SortOrder,
                string.IsNullOrWhiteSpace(x.Keywords) ? "-" : x.Keywords,
                x.ContentMarkdown.Length,
                x.PublishedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<DocArticleTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> CreateArticle(CancellationToken cancellationToken)
    {
        return View(await CreateArticleModelAsync(new DocArticleEditViewModel(), cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateArticle(DocArticleEditViewModel model, CancellationToken cancellationToken)
    {
        NormalizeArticleModel(model);
        await ValidateArticleAsync(model, null, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "docArticleTable"));
            }

            return View(await CreateArticleModelAsync(model, cancellationToken));
        }

        dbContext.DocArticles.Add(new DocArticle
        {
            CategoryId = model.CategoryId,
            Title = model.Title,
            Slug = model.Slug,
            Summary = model.Summary,
            ContentMarkdown = model.ContentMarkdown,
            Keywords = model.Keywords ?? string.Empty,
            Status = model.Status,
            SortOrder = model.SortOrder,
            PublishedAtUtc = model.Status == DocArticleStatus.Published ? DateTimeOffset.UtcNow : null
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档文章已创建。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> EditArticle(string id, CancellationToken cancellationToken)
    {
        var article = await dbContext.DocArticles.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (article is null)
        {
            return NotFound();
        }

        var model = new DocArticleEditViewModel
        {
            Id = article.ID,
            CategoryId = article.CategoryId,
            Title = article.Title,
            Slug = article.Slug,
            Summary = article.Summary,
            ContentMarkdown = article.ContentMarkdown,
            Keywords = article.Keywords,
            Status = article.Status,
            SortOrder = article.SortOrder
        };

        return View(await CreateArticleModelAsync(model, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditArticle(string id, DocArticleEditViewModel model, CancellationToken cancellationToken)
    {
        NormalizeArticleModel(model);
        await ValidateArticleAsync(model, id, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "docArticleTable"));
            }

            return View(await CreateArticleModelAsync(model, cancellationToken));
        }

        var article = await dbContext.DocArticles.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (article is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档文章不存在或已被删除。", "docArticleTable"));
            }

            return NotFound();
        }

        var wasPublished = article.Status == DocArticleStatus.Published;
        article.CategoryId = model.CategoryId;
        article.Title = model.Title;
        article.Slug = model.Slug;
        article.Summary = model.Summary;
        article.ContentMarkdown = model.ContentMarkdown;
        article.Keywords = model.Keywords ?? string.Empty;
        article.Status = model.Status;
        article.SortOrder = model.SortOrder;
        if (!wasPublished && model.Status == DocArticleStatus.Published)
        {
            article.PublishedAtUtc = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档文章已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishArticle(string id, CancellationToken cancellationToken)
    {
        var article = await dbContext.DocArticles.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (article is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档文章不存在或已被删除。", "docArticleTable"));
            }

            return NotFound();
        }

        article.Status = DocArticleStatus.Published;
        article.PublishedAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档文章已发布。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OfflineArticle(string id, CancellationToken cancellationToken)
    {
        var article = await dbContext.DocArticles.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (article is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档文章不存在或已被删除。", "docArticleTable"));
            }

            return NotFound();
        }

        article.Status = DocArticleStatus.Offline;
        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档文章已下架。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteArticle(string id, CancellationToken cancellationToken)
    {
        var article = await dbContext.DocArticles.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (article is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档文章不存在或已被删除。", "docArticleTable"));
            }

            return NotFound();
        }

        dbContext.DocArticles.Remove(article);
        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档文章已删除。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BatchArticles(string[] ids, string operation, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            const string emptyMessage = "请先选择要操作的文档文章。";
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(emptyMessage, "docArticleTable"));
            }

            TempData["AdminToast"] = emptyMessage;
            return RedirectToAction(nameof(Index));
        }

        var articles = await dbContext.DocArticles
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        if (operation == "delete")
        {
            dbContext.DocArticles.RemoveRange(articles);
            await dbContext.SaveChangesAsync(cancellationToken);
            var deleteMessage = $"已删除 {articles.Count} 篇文档文章。";
            if (IsAjaxRequest())
            {
                return Json(DrawerJson(deleteMessage, "docArticleTable", true));
            }

            TempData["AdminToast"] = deleteMessage;
            return RedirectToAction(nameof(Index));
        }

        foreach (var article in articles)
        {
            if (operation == "publish")
            {
                article.Status = DocArticleStatus.Published;
                article.PublishedAtUtc ??= DateTimeOffset.UtcNow;
            }
            else if (operation == "offline")
            {
                article.Status = DocArticleStatus.Offline;
            }
            else if (operation == "draft")
            {
                article.Status = DocArticleStatus.Draft;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var successMessage = $"已批量处理 {articles.Count} 篇文档文章。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docArticleTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Categories(string? keyword, bool? visible, CancellationToken cancellationToken)
    {
        var model = new DocCategoriesViewModel
        {
            Items = [],
            Keyword = keyword,
            Visible = visible,
            TotalCount = await dbContext.DocCategories.CountAsync(cancellationToken),
            VisibleCount = await dbContext.DocCategories.CountAsync(x => x.IsVisible, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> CategoryTableData(
        string? keyword,
        string? slug,
        bool? visible,
        int? sortMin,
        int? sortMax,
        int? articleMin,
        int? articleMax,
        int? publishedArticleMin,
        int? publishedArticleMax,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.DocCategories
            .AsNoTracking()
            .Include(x => x.Articles)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Name.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                (x.Description != null && x.Description.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var normalizedSlug = slug.Trim();
            query = query.Where(x => x.Slug.Contains(normalizedSlug));
        }

        if (visible is not null)
        {
            query = query.Where(x => x.IsVisible == visible);
        }

        if (sortMin is not null)
        {
            query = query.Where(x => x.SortOrder >= sortMin);
        }

        if (sortMax is not null)
        {
            query = query.Where(x => x.SortOrder <= sortMax);
        }

        if (articleMin is not null)
        {
            query = query.Where(x => x.Articles.Count >= articleMin);
        }

        if (articleMax is not null)
        {
            query = query.Where(x => x.Articles.Count <= articleMax);
        }

        if (publishedArticleMin is not null)
        {
            query = query.Where(x => x.Articles.Count(a => a.Status == DocArticleStatus.Published) >= publishedArticleMin);
        }

        if (publishedArticleMax is not null)
        {
            query = query.Where(x => x.Articles.Count(a => a.Status == DocArticleStatus.Published) <= publishedArticleMax);
        }

        var count = await query.CountAsync(cancellationToken);
        var categories = await ApplyCategorySorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = categories.Select(x => new DocCategoryTableItem(
                x.ID,
                x.Name,
                x.Slug,
                string.IsNullOrWhiteSpace(x.Description) ? "-" : x.Description,
                x.SortOrder,
                x.IsVisible,
                x.IsVisible ? "显示" : "隐藏",
                x.IsVisible ? "admin-badge-success" : "admin-badge-muted",
                x.Articles.Count,
                x.Articles.Count(a => a.Status == DocArticleStatus.Published),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<DocCategoryTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public IActionResult CreateCategory()
    {
        return View(new DocCategoryEditViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(DocCategoryEditViewModel model, CancellationToken cancellationToken)
    {
        NormalizeCategoryModel(model);
        await ValidateCategoryAsync(model, null, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "docCategoryTable"));
            }

            return View(model);
        }

        dbContext.DocCategories.Add(new DocCategory
        {
            Name = model.Name,
            Slug = model.Slug,
            Description = model.Description,
            SortOrder = model.SortOrder,
            IsVisible = model.IsVisible
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档分类已创建。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docCategoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "docCategoryTable");
        }

        return RedirectToAction(nameof(Categories));
    }

    public async Task<IActionResult> EditCategory(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            return NotFound();
        }

        return View(new DocCategoryEditViewModel
        {
            Id = category.ID,
            Name = category.Name,
            Slug = category.Slug,
            Description = category.Description,
            SortOrder = category.SortOrder,
            IsVisible = category.IsVisible
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditCategory(string id, DocCategoryEditViewModel model, CancellationToken cancellationToken)
    {
        if (model.Id != id)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson("文档分类参数不匹配。", "docCategoryTable"));
            }

            return BadRequest();
        }

        NormalizeCategoryModel(model);
        await ValidateCategoryAsync(model, id, cancellationToken);

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "docCategoryTable"));
            }

            return View(model);
        }

        var category = await dbContext.DocCategories.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档分类不存在或已被删除。", "docCategoryTable"));
            }

            return NotFound();
        }

        category.Name = model.Name;
        category.Slug = model.Slug;
        category.Description = model.Description;
        category.SortOrder = model.SortOrder;
        category.IsVisible = model.IsVisible;
        await dbContext.SaveChangesAsync(cancellationToken);

        const string successMessage = "文档分类已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docCategoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "docCategoryTable");
        }

        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleCategory(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档分类不存在或已被删除。", "docCategoryTable"));
            }

            return NotFound();
        }

        category.IsVisible = !category.IsVisible;
        await dbContext.SaveChangesAsync(cancellationToken);
        var successMessage = category.IsVisible ? "文档分类已显示。" : "文档分类已隐藏。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docCategoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(string id, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories
            .Include(x => x.Articles)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (category is null)
        {
            if (IsAjaxRequest())
            {
                return NotFound(DrawerJson("文档分类不存在或已被删除。", "docCategoryTable"));
            }

            return NotFound();
        }

        if (category.Articles.Count > 0)
        {
            var articleUrl = Url.Action(nameof(Index), "Docs", new { categoryId = category.ID }) ?? "/Docs";
            var message = $"分类“{category.Name}”下还有 {category.Articles.Count} 篇文档文章，暂时不能删除。请先调整这些文章的分类后再删除。";
            if (IsAjaxRequest())
            {
                return BadRequest(new
                {
                    success = false,
                    message,
                    tableId = "docCategoryTable",
                    articleUrl
                });
            }

            TempData["SettingError"] = message;
            return RedirectToAction(nameof(Categories));
        }

        dbContext.DocCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        const string successMessage = "文档分类已删除。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docCategoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BatchCategories(string[] ids, string operation, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            const string emptyMessage = "请先选择要操作的文档分类。";
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(emptyMessage, "docCategoryTable"));
            }

            TempData["AdminToast"] = emptyMessage;
            return RedirectToAction(nameof(Categories));
        }

        var categories = await dbContext.DocCategories
            .Include(x => x.Articles)
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        if (operation == "delete")
        {
            var deletable = categories.Where(x => x.Articles.Count == 0).ToList();
            dbContext.DocCategories.RemoveRange(deletable);
            await dbContext.SaveChangesAsync(cancellationToken);
            var message = deletable.Count == categories.Count
                ? $"已批量删除 {deletable.Count} 个文档分类。"
                : $"已删除 {deletable.Count} 个空分类，存在文章的分类已跳过。";
            if (IsAjaxRequest())
            {
                return Json(DrawerJson(message, "docCategoryTable", true));
            }

            TempData["AdminToast"] = message;
            return RedirectToAction(nameof(Categories));
        }

        foreach (var category in categories)
        {
            if (operation == "show")
            {
                category.IsVisible = true;
            }
            else if (operation == "hide")
            {
                category.IsVisible = false;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var successMessage = $"已批量处理 {categories.Count} 个文档分类。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "docCategoryTable", true));
        }

        TempData["AdminToast"] = successMessage;
        return RedirectToAction(nameof(Categories));
    }

    private async Task<DocArticleEditViewModel> CreateArticleModelAsync(DocArticleEditViewModel model, CancellationToken cancellationToken)
    {
        model.Categories = await GetCategoryOptionsAsync(false, cancellationToken);
        if (string.IsNullOrWhiteSpace(model.CategoryId) && model.Categories.Count > 0)
        {
            model.CategoryId = model.Categories[0].Id;
        }

        return model;
    }

    private async Task<IReadOnlyList<DocCategoryOption>> GetCategoryOptionsAsync(bool visibleOnly, CancellationToken cancellationToken)
    {
        var query = dbContext.DocCategories.AsNoTracking();
        if (visibleOnly)
        {
            query = query.Where(x => x.IsVisible);
        }

        return await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new DocCategoryOption(x.ID, x.Name))
            .ToListAsync(cancellationToken);
    }

    private async Task ValidateArticleAsync(DocArticleEditViewModel model, string? currentId, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ID == model.CategoryId, cancellationToken);
        if (category is null)
        {
            ModelState.AddModelError(nameof(model.CategoryId), "文档分类不存在");
        }

        var slugExists = await dbContext.DocArticles.AnyAsync(x => x.ID != currentId && x.CategoryId == model.CategoryId && x.Slug == model.Slug, cancellationToken);
        if (slugExists)
        {
            ModelState.AddModelError(nameof(model.Slug), "同一分类下文章标识已存在");
        }

        if (category is not null && ValidateMarkdownImageRoutes(model.ContentMarkdown, category.Slug, model.Slug) is { } imageValidation)
        {
            ModelState.AddModelError(nameof(model.ContentMarkdown), imageValidation);
        }
    }

    private async Task ValidateCategoryAsync(DocCategoryEditViewModel model, string? currentId, CancellationToken cancellationToken)
    {
        var slugExists = await dbContext.DocCategories.AnyAsync(x => x.ID != currentId && x.Slug == model.Slug, cancellationToken);
        if (slugExists)
        {
            ModelState.AddModelError(nameof(model.Slug), "分类标识已存在");
        }

        if (!string.IsNullOrWhiteSpace(currentId))
        {
            var currentCategory = await dbContext.DocCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ID == currentId, cancellationToken);
            if (currentCategory is not null &&
                !string.Equals(currentCategory.Slug, model.Slug, StringComparison.Ordinal) &&
                await dbContext.DocArticles.AnyAsync(x => x.CategoryId == currentId, cancellationToken))
            {
                ModelState.AddModelError(nameof(model.Slug), "分类下已有文章时不能修改分类标识，避免文章图片路由失效");
            }
        }
    }

    private string? ValidateMarkdownImageRoutes(string markdown, string categorySlug, string articleSlug)
    {
        var expectedPrefix = mediaPathService.BuildArticleImagePrefix(categorySlug, articleSlug);
        foreach (Match match in MarkdownImageRegex.Matches(markdown))
        {
            var url = match.Groups["url"].Value.Trim().Trim('<', '>');
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                return $"Markdown 图片必须使用 {expectedPrefix} 路由，当前发现：{url}";
            }
        }

        return null;
    }

    private static IQueryable<DocArticle> ApplyArticleSorting(IQueryable<DocArticle> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "title" => isAscending ? query.OrderBy(x => x.Title) : query.OrderByDescending(x => x.Title),
            "slug" => isAscending ? query.OrderBy(x => x.Slug) : query.OrderByDescending(x => x.Slug),
            "categoryName" => isAscending ? query.OrderBy(x => x.Category == null ? string.Empty : x.Category.Name) : query.OrderByDescending(x => x.Category == null ? string.Empty : x.Category.Name),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "sortOrder" => isAscending ? query.OrderBy(x => x.SortOrder) : query.OrderByDescending(x => x.SortOrder),
            "contentLength" => isAscending ? query.OrderBy(x => x.ContentMarkdown.Length) : query.OrderByDescending(x => x.ContentMarkdown.Length),
            "publishedAtText" => isAscending ? query.OrderBy(x => x.PublishedAtUtc) : query.OrderByDescending(x => x.PublishedAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderBy(x => x.Category == null ? string.Empty : x.Category.Name).ThenBy(x => x.SortOrder).ThenBy(x => x.Title)
        };
    }

    private static IQueryable<DocCategory> ApplyCategorySorting(IQueryable<DocCategory> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "name" => isAscending ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
            "slug" => isAscending ? query.OrderBy(x => x.Slug) : query.OrderByDescending(x => x.Slug),
            "sortOrder" => isAscending ? query.OrderBy(x => x.SortOrder) : query.OrderByDescending(x => x.SortOrder),
            "isVisible" => isAscending ? query.OrderBy(x => x.IsVisible) : query.OrderByDescending(x => x.IsVisible),
            "articleCount" => isAscending ? query.OrderBy(x => x.Articles.Count) : query.OrderByDescending(x => x.Articles.Count),
            "publishedArticleCount" => isAscending ? query.OrderBy(x => x.Articles.Count(a => a.Status == DocArticleStatus.Published)) : query.OrderByDescending(x => x.Articles.Count(a => a.Status == DocArticleStatus.Published)),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
        };
    }

    private static void NormalizeArticleModel(DocArticleEditViewModel model)
    {
        model.CategoryId = model.CategoryId?.Trim() ?? string.Empty;
        model.Title = model.Title?.Trim() ?? string.Empty;
        model.Slug = model.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        model.Summary = model.Summary?.Trim() ?? string.Empty;
        model.ContentMarkdown = model.ContentMarkdown?.Trim() ?? string.Empty;
        model.Keywords = model.Keywords?.Trim();
    }

    private static void NormalizeCategoryModel(DocCategoryEditViewModel model)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Slug = model.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
    }

    private bool IsDrawerRequest()
    {
        return string.Equals(Request.Query["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase)
            || (Request.HasFormContentType && string.Equals(Request.Form["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers.XRequestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string GetModelStateMessage()
    {
        return ModelState.Values
            .SelectMany(x => x.Errors)
            .Select(x => x.ErrorMessage)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "提交失败，请检查表单内容。";
    }

    private static object DrawerJson(string message, string tableId, bool success = false)
    {
        return new
        {
            success,
            message,
            tableId
        };
    }

    private ContentResult DrawerSuccess(string message, string tableId)
    {
        var encodedMessage = JsonSerializer.Serialize(message);
        var encodedTableId = JsonSerializer.Serialize(tableId);
        var html = "<!DOCTYPE html><html lang=\"zh-CN\"><body><script>"
            + "if (parent && parent.layui) {"
            + $"if (parent.layui.table) parent.layui.table.reload({encodedTableId});"
            + $"if (parent.layui.layer) parent.layui.layer.msg({encodedMessage});"
            + "}"
            + "if (parent && parent.layer && window.name) {"
            + "parent.layer.close(parent.layer.getFrameIndex(window.name));"
            + "} else {"
            + "location.href = document.referrer || \"/Docs/Categories\";"
            + "}"
            + "</script></body></html>";

        return Content(html, "text/html");
    }

    private static string GetStatusName(DocArticleStatus status) => status switch
    {
        DocArticleStatus.Draft => "草稿",
        DocArticleStatus.Published => "已发布",
        DocArticleStatus.Offline => "下架",
        _ => status.ToString()
    };

    private static string GetStatusBadgeClass(DocArticleStatus status) => status switch
    {
        DocArticleStatus.Published => "admin-badge-success",
        DocArticleStatus.Offline => "admin-badge-danger",
        _ => "admin-badge-warning"
    };
}
