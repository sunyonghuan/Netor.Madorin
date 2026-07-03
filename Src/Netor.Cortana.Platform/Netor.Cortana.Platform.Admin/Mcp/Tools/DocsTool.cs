using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Mcp.Tools;

[McpServerToolType]
public sealed class DocsTool
{
    private const string CategoriesListToolName = "docs_categories_list";
    private const string CategoriesUpsertToolName = "docs_categories_upsert";
    private const string ArticlesSearchToolName = "docs_articles_search";
    private const string ArticlesGetToolName = "docs_articles_get";
    private const string ArticlesUpsertToolName = "docs_articles_upsert";
    private const string ArticlesSetStatusToolName = "docs_articles_set_status";
    private const string ImagesUploadToolName = "docs_images_upload";

    [McpServerTool(Name = CategoriesListToolName, ReadOnly = true, OpenWorld = false)]
    [Description("列出帮助中心文档分类。pageSize 最大 20。错误码：VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<DocCategorySearchResult>> ListCategoriesAsync(
        DocsCategoriesListInput input,
        DocOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.ListCategoriesAsync(
            input.Keyword,
            input.Visible,
            input.Page,
            input.PageSize,
            cancellationToken);
    }

    [McpServerTool(Name = CategoriesUpsertToolName, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("创建或更新帮助中心文档分类。仅超级管理员可用。已有文章的分类不能通过 MCP 修改 slug，避免图片路由失效。建议传 requestId。")]
    public static async Task<OperationResult<DocCategoryResult>> UpsertCategoryAsync(
        DocsCategoryUpsertInput input,
        DocOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<DocCategoryResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            CategoriesUpsertToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultDocCategoryResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.UpsertCategoryAsync(
            input.CategoryId,
            input.Name,
            input.Slug,
            input.Description,
            input.SortOrder,
            input.IsVisible,
            cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, CategoriesUpsertToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                CategoriesUpsertToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultDocCategoryResult,
                cancellationToken);
        }

        return result;
    }

    [McpServerTool(Name = ArticlesSearchToolName, ReadOnly = true, OpenWorld = false)]
    [Description("搜索帮助中心文章。pageSize 最大 20。可按分类ID、分类slug和状态筛选。错误码：VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<DocArticleSearchResult>> SearchArticlesAsync(
        DocsArticlesSearchInput input,
        DocOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.SearchArticlesAsync(
            input.Keyword,
            input.CategoryId,
            input.CategorySlug,
            input.Status,
            input.Page,
            input.PageSize,
            cancellationToken);
    }

    [McpServerTool(Name = ArticlesGetToolName, ReadOnly = true, OpenWorld = false)]
    [Description("获取帮助中心文章详情。可传 articleId，或传 categorySlug + articleSlug。错误码：NOT_FOUND / VALIDATION / INTERNAL。")]
    public static async Task<OperationResult<DocArticleDetailResult>> GetArticleAsync(
        DocsArticleGetInput input,
        DocOperations operations,
        CancellationToken cancellationToken)
    {
        return await operations.GetArticleAsync(
            input.ArticleId,
            input.CategorySlug,
            input.ArticleSlug,
            cancellationToken);
    }

    [McpServerTool(Name = ArticlesUpsertToolName, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("创建或更新帮助中心 Markdown 文章。仅超级管理员可用。正文中的本地图片必须使用 /docs-media/{categorySlug}/{articleSlug}/ 路由，保证 MCP、后台预览、官网三端都能显示。建议传 requestId。")]
    public static async Task<OperationResult<DocArticleDetailResult>> UpsertArticleAsync(
        DocsArticleUpsertInput input,
        DocOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<DocArticleDetailResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            ArticlesUpsertToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultDocArticleDetailResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.UpsertArticleAsync(
            input.ArticleId,
            input.CategoryId,
            input.CategorySlug,
            input.Title,
            input.Slug,
            input.Summary,
            input.ContentMarkdown,
            input.Keywords,
            input.Status,
            input.SortOrder,
            cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, ArticlesUpsertToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                ArticlesUpsertToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultDocArticleDetailResult,
                cancellationToken);
        }

        return result;
    }

    [McpServerTool(Name = ArticlesSetStatusToolName, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("设置帮助中心文章状态。仅超级管理员可用。建议传 requestId。错误码：NOT_FOUND / FORBIDDEN / VALIDATION / IDEMPOTENT_REPLAY / INTERNAL。")]
    public static async Task<OperationResult<DocArticleStatusResult>> SetArticleStatusAsync(
        DocsArticleSetStatusInput input,
        DocOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<DocArticleStatusResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            ArticlesSetStatusToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultDocArticleStatusResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.SetArticleStatusAsync(input.ArticleId, input.Status, cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, ArticlesSetStatusToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                ArticlesSetStatusToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultDocArticleStatusResult,
                cancellationToken);
        }

        return result;
    }

    [McpServerTool(Name = ImagesUploadToolName, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("上传帮助中心文章图片到共享目录。仅超级管理员可用。返回 /docs-media/{categorySlug}/{articleSlug}/{fileName} 和可直接写入正文的 Markdown 图片片段。建议传 requestId。")]
    public static async Task<OperationResult<DocImageUploadResult>> UploadImageAsync(
        DocsImageUploadInput input,
        DocOperations operations,
        AdminMcpContext context,
        AdminMcpIdempotencyService idempotency,
        AdminAuditService audit,
        CancellationToken cancellationToken)
    {
        if (context.RequireSuperAdmin<DocImageUploadResult>() is { } forbidden)
        {
            return forbidden;
        }

        var replay = await idempotency.TryReplayAsync(
            context.ManagerId,
            ImagesUploadToolName,
            input.RequestId,
            AdminMcpJsonContext.Default.OperationResultDocImageUploadResult,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await operations.UploadImageAsync(
            input.CategorySlug,
            input.ArticleSlug,
            input.FileName,
            input.Base64Data,
            input.AltText,
            cancellationToken);
        stopwatch.Stop();

        await LogAsync(audit, context, ImagesUploadToolName, result.Success, result.ErrorCode, stopwatch, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(input.RequestId))
        {
            await idempotency.RecordAsync(
                context.ManagerId,
                ImagesUploadToolName,
                input.RequestId,
                result,
                AdminMcpJsonContext.Default.OperationResultDocImageUploadResult,
                cancellationToken);
        }

        return result;
    }

    private static async Task LogAsync(
        AdminAuditService audit,
        AdminMcpContext context,
        string toolName,
        bool success,
        string? errorCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await audit.LogAsync(new AdminAuditEntry(
            ManagerId: context.ManagerId,
            TokenId: context.TokenId,
            ToolName: toolName,
            Source: "MCP",
            Ip: context.RemoteIp,
            Ua: context.UserAgent,
            Success: success,
            ErrorCode: errorCode,
            DurationMs: (int)stopwatch.ElapsedMilliseconds),
            cancellationToken);
    }
}

public sealed record DocsCategoriesListInput(
    [property: Description("搜索关键字，可匹配分类名称、slug 或描述。")]
    string? Keyword,
    [property: Description("是否只返回显示或隐藏分类。")]
    bool? Visible,
    [property: Description("页码，从 1 开始。")]
    int Page = 1,
    [property: Description("每页数量，最大 20。")]
    int PageSize = 20);

public sealed record DocsCategoryUpsertInput(
    [property: Description("分类ID。为空则创建，传入则更新。")]
    string? CategoryId,
    [property: Description("分类名称。")]
    string Name,
    [property: Description("分类 slug，小写字母、数字和中横线。")]
    string Slug,
    [property: Description("分类描述。")]
    string? Description,
    [property: Description("排序值，越小越靠前。")]
    int SortOrder,
    [property: Description("是否在官网显示。")]
    bool IsVisible,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);

public sealed record DocsArticlesSearchInput(
    [property: Description("搜索关键字，可匹配标题、slug、摘要、关键词或正文。")]
    string? Keyword,
    [property: Description("分类ID。")]
    string? CategoryId,
    [property: Description("分类 slug。")]
    string? CategorySlug,
    [property: Description("文章状态：1=草稿，2=已发布，3=下架。")]
    DocArticleStatus? Status,
    [property: Description("页码，从 1 开始。")]
    int Page = 1,
    [property: Description("每页数量，最大 20。")]
    int PageSize = 20);

public sealed record DocsArticleGetInput(
    [property: Description("文章ID。优先使用 articleId。")]
    string? ArticleId,
    [property: Description("分类 slug。未传 articleId 时必填。")]
    string? CategorySlug,
    [property: Description("文章 slug。未传 articleId 时必填。")]
    string? ArticleSlug);

public sealed record DocsArticleUpsertInput(
    [property: Description("文章ID。为空则创建，传入则更新。")]
    string? ArticleId,
    [property: Description("分类ID。优先使用 categoryId。")]
    string? CategoryId,
    [property: Description("分类 slug。未传 categoryId 时必填。")]
    string? CategorySlug,
    [property: Description("文章标题。")]
    string Title,
    [property: Description("文章 slug，小写字母、数字和中横线。")]
    string Slug,
    [property: Description("文章摘要。")]
    string Summary,
    [property: Description("Markdown 正文。本地图片必须使用 /docs-media/{categorySlug}/{articleSlug}/ 路由。")]
    string ContentMarkdown,
    [property: Description("搜索关键词，逗号分隔。")]
    string? Keywords,
    [property: Description("文章状态：1=草稿，2=已发布，3=下架。")]
    DocArticleStatus Status,
    [property: Description("排序值，越小越靠前。")]
    int SortOrder,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);

public sealed record DocsArticleSetStatusInput(
    [property: Description("文章ID。")]
    string ArticleId,
    [property: Description("文章状态：1=草稿，2=已发布，3=下架。")]
    DocArticleStatus Status,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);

public sealed record DocsImageUploadInput(
    [property: Description("分类 slug，用于生成 /docs-media/{categorySlug}/... 路由。")]
    string CategorySlug,
    [property: Description("文章 slug，用于生成 /docs-media/{categorySlug}/{articleSlug}/... 路由。")]
    string ArticleSlug,
    [property: Description("图片文件名。仅允许 png、jpg、jpeg、gif、webp。")]
    string FileName,
    [property: Description("图片 Base64 内容，可带 data:image/...;base64, 前缀。")]
    string Base64Data,
    [property: Description("图片替代文本，用于生成 Markdown。")]
    string? AltText,
    [property: Description("幂等请求ID，建议使用 UUID/ULID。")]
    string? RequestId);
