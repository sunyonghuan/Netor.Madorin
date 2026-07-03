using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Docs;
using Netor.Cortana.Platform.Services.Docs;

namespace Netor.Cortana.Platform.Admin.Operations;

public sealed class DocOperations(
    PlatformDbContext dbContext,
    DocsMediaPathService mediaPathService,
    IWebHostEnvironment environment,
    TimeProvider timeProvider)
{
    private static readonly Regex SlugRegex = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp"
    };

    public async Task<OperationResult<DocCategorySearchResult>> ListCategoriesAsync(
        string? keyword,
        bool? visible,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);

        var query = dbContext.DocCategories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Name.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                (x.Description != null && x.Description.Contains(normalizedKeyword)));
        }

        if (visible is not null)
        {
            query = query.Where(x => x.IsVisible == visible);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.ID,
                x.Name,
                x.Slug,
                x.Description,
                x.SortOrder,
                x.IsVisible,
                ArticleCount = x.Articles.Count,
                PublishedArticleCount = x.Articles.Count(a => a.Status == DocArticleStatus.Published)
            })
            .ToListAsync(cancellationToken);
        var items = rows
            .Select(x => new DocCategoryResult(
                x.ID,
                x.Name,
                x.Slug,
                x.Description ?? string.Empty,
                x.SortOrder,
                x.IsVisible,
                x.ArticleCount,
                x.PublishedArticleCount))
            .ToList();

        return OperationResult<DocCategorySearchResult>.Ok(new DocCategorySearchResult(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<DocCategoryResult>> UpsertCategoryAsync(
        string? categoryId,
        string name,
        string slug,
        string? description,
        int sortOrder,
        bool isVisible,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeRequiredText(name, 64);
        var normalizedSlug = NormalizeSlug(slug);
        var normalizedDescription = NormalizeOptionalText(description, 256);

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return OperationResult<DocCategoryResult>.Fail(OperationErrorCodes.Validation, "分类名称不能为空。");
        }

        if (!IsValidSlug(normalizedSlug))
        {
            return OperationResult<DocCategoryResult>.Fail(OperationErrorCodes.Validation, "分类标识只能使用小写字母、数字和中横线。");
        }

        DocCategory category;
        var isCreate = string.IsNullOrWhiteSpace(categoryId);
        if (isCreate)
        {
            category = new DocCategory();
            dbContext.DocCategories.Add(category);
        }
        else
        {
            var normalizedCategoryId = categoryId!.Trim();
            var existingCategory = await dbContext.DocCategories.FirstOrDefaultAsync(x => x.ID == normalizedCategoryId, cancellationToken);
            if (existingCategory is null)
            {
                return OperationResult<DocCategoryResult>.Fail(OperationErrorCodes.NotFound, "文档分类不存在或已被删除。");
            }

            category = existingCategory;
            if (!string.Equals(category.Slug, normalizedSlug, StringComparison.Ordinal) &&
                await dbContext.DocArticles.AnyAsync(x => x.CategoryId == category.ID, cancellationToken))
            {
                return OperationResult<DocCategoryResult>.Fail(OperationErrorCodes.Validation, "分类下已有文章时不能通过 MCP 修改分类标识，避免文章图片路由失效。");
            }
        }

        var slugExists = await dbContext.DocCategories
            .AnyAsync(x => x.ID != category.ID && x.Slug == normalizedSlug, cancellationToken);
        if (slugExists)
        {
            return OperationResult<DocCategoryResult>.Fail(OperationErrorCodes.Conflict, "分类标识已存在。");
        }

        category.Name = normalizedName;
        category.Slug = normalizedSlug;
        category.Description = normalizedDescription;
        category.SortOrder = sortOrder;
        category.IsVisible = isVisible;
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await CreateCategoryResultAsync(category.ID, cancellationToken);
        return OperationResult<DocCategoryResult>.Ok(result, isCreate ? "文档分类已创建。" : "文档分类已保存。");
    }

    public async Task<OperationResult<DocArticleSearchResult>> SearchArticlesAsync(
        string? keyword,
        string? categoryId,
        string? categorySlug,
        DocArticleStatus? status,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);

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
            var normalizedCategoryId = categoryId.Trim();
            query = query.Where(x => x.CategoryId == normalizedCategoryId);
        }

        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var normalizedCategorySlug = NormalizeSlug(categorySlug);
            query = query.Where(x => x.Category != null && x.Category.Slug == normalizedCategorySlug);
        }

        if (status is not null)
        {
            if (!IsKnownArticleStatus(status.Value))
            {
                return OperationResult<DocArticleSearchResult>.Fail(OperationErrorCodes.Validation, "文章状态只能是 1=草稿、2=已发布、3=下架。");
            }

            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(x => x.Category == null ? string.Empty : x.Category.Slug)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Title)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.ID,
                x.Title,
                x.Slug,
                x.Summary,
                x.Status,
                x.SortOrder,
                x.PublishedAtUtc,
                x.CategoryId,
                CategoryName = x.Category == null ? string.Empty : x.Category.Name,
                CategorySlug = x.Category == null ? string.Empty : x.Category.Slug
            })
            .ToListAsync(cancellationToken);
        var items = rows
            .Select(x => new DocArticleSearchItem(
                x.ID,
                x.Title,
                x.Slug,
                x.Summary,
                x.CategoryId,
                x.CategoryName,
                x.CategorySlug,
                x.Status,
                GetStatusName(x.Status),
                x.SortOrder,
                x.PublishedAtUtc,
                mediaPathService.BuildArticleImagePrefix(x.CategorySlug, x.Slug)))
            .ToList();

        return OperationResult<DocArticleSearchResult>.Ok(new DocArticleSearchResult(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<DocArticleDetailResult>> GetArticleAsync(
        string? articleId,
        string? categorySlug,
        string? articleSlug,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.DocArticles
            .AsNoTracking()
            .Include(x => x.Category)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(articleId))
        {
            var normalizedArticleId = articleId.Trim();
            query = query.Where(x => x.ID == normalizedArticleId);
        }
        else
        {
            var normalizedCategorySlug = NormalizeSlug(categorySlug);
            var normalizedArticleSlug = NormalizeSlug(articleSlug);
            if (!IsValidSlug(normalizedCategorySlug) || !IsValidSlug(normalizedArticleSlug))
            {
                return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "请提供 articleId，或提供合法的 categorySlug 与 articleSlug。");
            }

            query = query.Where(x =>
                x.Category != null &&
                x.Category.Slug == normalizedCategorySlug &&
                x.Slug == normalizedArticleSlug);
        }

        var article = await query.FirstOrDefaultAsync(cancellationToken);
        if (article?.Category is null)
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.NotFound, "文档文章不存在或已被删除。");
        }

        return OperationResult<DocArticleDetailResult>.Ok(ToArticleDetailResult(article, article.Category));
    }

    public async Task<OperationResult<DocArticleDetailResult>> UpsertArticleAsync(
        string? articleId,
        string? categoryId,
        string? categorySlug,
        string title,
        string slug,
        string summary,
        string contentMarkdown,
        string? keywords,
        DocArticleStatus status,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        var category = await ResolveCategoryAsync(categoryId, categorySlug, cancellationToken);
        if (category is null)
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.NotFound, "文档分类不存在或已被删除。");
        }

        var normalizedTitle = NormalizeRequiredText(title, 128);
        var normalizedSlug = NormalizeSlug(slug);
        var normalizedSummary = NormalizeRequiredText(summary, 256);
        var normalizedContent = contentMarkdown?.Trim() ?? string.Empty;
        var normalizedKeywords = NormalizeOptionalText(keywords, 512) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "文章标题不能为空。");
        }

        if (!IsValidSlug(normalizedSlug))
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "文章标识只能使用小写字母、数字和中横线。");
        }

        if (string.IsNullOrWhiteSpace(normalizedSummary))
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "文章摘要不能为空。");
        }

        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "文章正文不能为空。");
        }

        if (!IsKnownArticleStatus(status))
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, "文章状态只能是 1=草稿、2=已发布、3=下架。");
        }

        if (ValidateMarkdownImageRoutes(normalizedContent, category.Slug, normalizedSlug) is { } imageValidation)
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Validation, imageValidation);
        }

        DocArticle article;
        var isCreate = string.IsNullOrWhiteSpace(articleId);
        if (isCreate)
        {
            article = new DocArticle();
            dbContext.DocArticles.Add(article);
        }
        else
        {
            var normalizedArticleId = articleId!.Trim();
            var existingArticle = await dbContext.DocArticles.FirstOrDefaultAsync(x => x.ID == normalizedArticleId, cancellationToken);
            if (existingArticle is null)
            {
                return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.NotFound, "文档文章不存在或已被删除。");
            }

            article = existingArticle;
        }

        var slugExists = await dbContext.DocArticles
            .AnyAsync(x => x.ID != article.ID && x.CategoryId == category.ID && x.Slug == normalizedSlug, cancellationToken);
        if (slugExists)
        {
            return OperationResult<DocArticleDetailResult>.Fail(OperationErrorCodes.Conflict, "同一分类下文章标识已存在。");
        }

        var wasPublished = article.Status == DocArticleStatus.Published;
        article.CategoryId = category.ID;
        article.Title = normalizedTitle;
        article.Slug = normalizedSlug;
        article.Summary = normalizedSummary;
        article.ContentMarkdown = normalizedContent;
        article.Keywords = normalizedKeywords;
        article.Status = status;
        article.SortOrder = sortOrder;
        if (!wasPublished && status == DocArticleStatus.Published)
        {
            article.PublishedAtUtc = timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<DocArticleDetailResult>.Ok(ToArticleDetailResult(article, category), isCreate ? "文档文章已创建。" : "文档文章已保存。");
    }

    public async Task<OperationResult<DocArticleStatusResult>> SetArticleStatusAsync(
        string articleId,
        DocArticleStatus status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(articleId))
        {
            return OperationResult<DocArticleStatusResult>.Fail(OperationErrorCodes.Validation, "文章ID不能为空。");
        }

        if (!IsKnownArticleStatus(status))
        {
            return OperationResult<DocArticleStatusResult>.Fail(OperationErrorCodes.Validation, "文章状态只能是 1=草稿、2=已发布、3=下架。");
        }

        var article = await dbContext.DocArticles
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.ID == articleId, cancellationToken);
        if (article?.Category is null)
        {
            return OperationResult<DocArticleStatusResult>.Fail(OperationErrorCodes.NotFound, "文档文章不存在或已被删除。");
        }

        article.Status = status;
        if (status == DocArticleStatus.Published)
        {
            article.PublishedAtUtc ??= timeProvider.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<DocArticleStatusResult>.Ok(
            new DocArticleStatusResult(article.ID, article.Status, GetStatusName(article.Status), article.PublishedAtUtc),
            $"文章状态已更新为 {GetStatusName(article.Status)}。");
    }

    public async Task<OperationResult<DocImageUploadResult>> UploadImageAsync(
        string categorySlug,
        string articleSlug,
        string fileName,
        string base64Data,
        string? altText,
        CancellationToken cancellationToken = default)
    {
        var normalizedCategorySlug = NormalizeSlug(categorySlug);
        var normalizedArticleSlug = NormalizeSlug(articleSlug);
        if (!IsValidSlug(normalizedCategorySlug) || !IsValidSlug(normalizedArticleSlug))
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, "categorySlug 与 articleSlug 只能使用小写字母、数字和中横线。");
        }

        var categoryExists = await dbContext.DocCategories
            .AsNoTracking()
            .AnyAsync(x => x.Slug == normalizedCategorySlug, cancellationToken);
        if (!categoryExists)
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.NotFound, "文档分类不存在或已被删除。");
        }

        var safeFileName = SanitizeImageFileName(fileName);
        if (safeFileName is null)
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, "图片文件名无效，且扩展名只能是 png、jpg、jpeg、gif、webp。");
        }

        var bytesResult = TryDecodeImage(base64Data);
        if (!bytesResult.Success)
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, bytesResult.ErrorMessage);
        }

        var bytes = bytesResult.Bytes;
        if (bytes.Length > mediaPathService.MaxImageBytes)
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, $"图片不能超过 {mediaPathService.MaxImageBytes} 字节。");
        }

        if (!MatchesImageSignature(bytes, Path.GetExtension(safeFileName)))
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, "图片内容与文件扩展名不匹配。");
        }

        var articleDirectory = mediaPathService.GetArticleDirectory(environment.ContentRootPath, normalizedCategorySlug, normalizedArticleSlug);
        Directory.CreateDirectory(articleDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(articleDirectory, safeFileName));
        if (!IsPathInsideDirectory(fullPath, articleDirectory))
        {
            return OperationResult<DocImageUploadResult>.Fail(OperationErrorCodes.Validation, "图片路径非法。");
        }

        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        var url = mediaPathService.BuildArticleImageUrl(normalizedCategorySlug, normalizedArticleSlug, safeFileName);
        var markdown = $"![{NormalizeAltText(altText)}]({url})";
        return OperationResult<DocImageUploadResult>.Ok(
            new DocImageUploadResult(normalizedCategorySlug, normalizedArticleSlug, safeFileName, url, markdown, bytes.Length),
            "图片已上传。");
    }

    private async Task<DocCategoryResult> CreateCategoryResultAsync(string categoryId, CancellationToken cancellationToken)
    {
        var category = await dbContext.DocCategories
            .AsNoTracking()
            .Where(x => x.ID == categoryId)
            .Select(x => new
            {
                x.ID,
                x.Name,
                x.Slug,
                x.Description,
                x.SortOrder,
                x.IsVisible,
                ArticleCount = x.Articles.Count,
                PublishedArticleCount = x.Articles.Count(a => a.Status == DocArticleStatus.Published)
            })
            .FirstAsync(cancellationToken);

        return new DocCategoryResult(
            category.ID,
            category.Name,
            category.Slug,
            category.Description ?? string.Empty,
            category.SortOrder,
            category.IsVisible,
            category.ArticleCount,
            category.PublishedArticleCount);
    }

    private async Task<DocCategory?> ResolveCategoryAsync(string? categoryId, string? categorySlug, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            var normalizedCategoryId = categoryId.Trim();
            return await dbContext.DocCategories.FirstOrDefaultAsync(x => x.ID == normalizedCategoryId, cancellationToken);
        }

        var normalizedCategorySlug = NormalizeSlug(categorySlug);
        if (!IsValidSlug(normalizedCategorySlug))
        {
            return null;
        }

        return await dbContext.DocCategories.FirstOrDefaultAsync(x => x.Slug == normalizedCategorySlug, cancellationToken);
    }

    private DocArticleDetailResult ToArticleDetailResult(DocArticle article, DocCategory category)
    {
        var imagePrefix = mediaPathService.BuildArticleImagePrefix(category.Slug, article.Slug);
        return new DocArticleDetailResult(
            article.ID,
            article.Title,
            article.Slug,
            article.Summary,
            article.CategoryId,
            category.Name,
            category.Slug,
            article.ContentMarkdown,
            article.Keywords,
            article.Status,
            GetStatusName(article.Status),
            article.SortOrder,
            article.PublishedAtUtc,
            $"/docs/{category.Slug}/{article.Slug}",
            imagePrefix);
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

    private static ImageDecodeResult TryDecodeImage(string base64Data)
    {
        if (string.IsNullOrWhiteSpace(base64Data))
        {
            return ImageDecodeResult.Fail("图片 Base64 不能为空。");
        }

        var normalized = base64Data.Trim();
        var commaIndex = normalized.IndexOf(',', StringComparison.Ordinal);
        if (normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            normalized = normalized[(commaIndex + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == 0
                ? ImageDecodeResult.Fail("图片内容不能为空。")
                : ImageDecodeResult.Ok(bytes);
        }
        catch (FormatException)
        {
            return ImageDecodeResult.Fail("图片 Base64 格式无效。");
        }
    }

    private static string? SanitizeImageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var rawFileName = Path.GetFileName(fileName.Trim());
        var extension = Path.GetExtension(rawFileName).ToLowerInvariant();
        if (!AllowedImageExtensions.Contains(extension))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(rawFileName).ToLowerInvariant();
        var sanitizedChars = name.Select(ch =>
            ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' ? ch : '-');
        var sanitized = string.Join(string.Empty, sanitizedChars).Trim('-');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "image";
        }

        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].Trim('-');
        }

        return $"{sanitized}{extension}";
    }

    private static bool MatchesImageSignature(byte[] bytes, string extension)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47 &&
                bytes[4] == 0x0D &&
                bytes[5] == 0x0A &&
                bytes[6] == 0x1A &&
                bytes[7] == 0x0A;
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^1] == 0xD9;
        }

        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 6 &&
                bytes[0] == 0x47 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46 &&
                bytes[3] == 0x38;
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 12 &&
                bytes[0] == 0x52 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46 &&
                bytes[3] == 0x46 &&
                bytes[8] == 0x57 &&
                bytes[9] == 0x45 &&
                bytes[10] == 0x42 &&
                bytes[11] == 0x50;
        }

        return false;
    }

    private static bool IsKnownArticleStatus(DocArticleStatus status)
        => status is DocArticleStatus.Draft or DocArticleStatus.Published or DocArticleStatus.Offline;

    private static bool IsValidSlug(string value)
        => !string.IsNullOrWhiteSpace(value) && SlugRegex.IsMatch(value);

    private static string NormalizeSlug(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeRequiredText(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NormalizeOptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string NormalizeAltText(string? altText)
    {
        var normalized = string.IsNullOrWhiteSpace(altText) ? "图片" : altText.Trim();
        return normalized.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string GetStatusName(DocArticleStatus status) => status switch
    {
        DocArticleStatus.Draft => "草稿",
        DocArticleStatus.Published => "已发布",
        DocArticleStatus.Offline => "下架",
        _ => status.ToString()
    };

    private static bool IsPathInsideDirectory(string fullPath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, fullPath);
        return relativePath == "." ||
            (!relativePath.StartsWith("..", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relativePath));
    }

    private sealed record ImageDecodeResult(bool Success, byte[] Bytes, string ErrorMessage)
    {
        public static ImageDecodeResult Ok(byte[] bytes) => new(true, bytes, string.Empty);

        public static ImageDecodeResult Fail(string errorMessage) => new(false, [], errorMessage);
    }
}

public sealed record DocCategorySearchResult(
    IReadOnlyList<DocCategoryResult> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record DocCategoryResult(
    string Id,
    string Name,
    string Slug,
    string Description,
    int SortOrder,
    bool IsVisible,
    int ArticleCount,
    int PublishedArticleCount);

public sealed record DocArticleSearchResult(
    IReadOnlyList<DocArticleSearchItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record DocArticleSearchItem(
    string Id,
    string Title,
    string Slug,
    string Summary,
    string CategoryId,
    string CategoryName,
    string CategorySlug,
    DocArticleStatus Status,
    string StatusName,
    int SortOrder,
    DateTimeOffset? PublishedAtUtc,
    string ImageRoutePrefix);

public sealed record DocArticleDetailResult(
    string Id,
    string Title,
    string Slug,
    string Summary,
    string CategoryId,
    string CategoryName,
    string CategorySlug,
    string ContentMarkdown,
    string Keywords,
    DocArticleStatus Status,
    string StatusName,
    int SortOrder,
    DateTimeOffset? PublishedAtUtc,
    string ArticleUrl,
    string ImageRoutePrefix);

public sealed record DocArticleStatusResult(
    string ArticleId,
    DocArticleStatus Status,
    string StatusName,
    DateTimeOffset? PublishedAtUtc);

public sealed record DocImageUploadResult(
    string CategorySlug,
    string ArticleSlug,
    string FileName,
    string Url,
    string Markdown,
    int ContentLength);
