using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Reviews;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Reviews;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Reviews;
using Netor.Cortana.Platform.Services.Risk;
using Netor.Cortana.Platform.Services.Solutions;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class ReviewsController(
    PlatformDbContext dbContext,
    AssetReviewService reviewService,
    PackageRiskService packageRiskService,
    PackageStorageService packageStorageService) : Controller
{
    public async Task<IActionResult> Index(
        string? keyword,
        AssetReviewStatus? status,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.AssetReviews
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .Include(x => x.SubmitterAccount)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Notes.Contains(normalizedKeyword) ||
                (x.Asset != null && (x.Asset.Name.Contains(normalizedKeyword) || x.Asset.Slug.Contains(normalizedKeyword))) ||
                (x.SubmitterAccount != null && x.SubmitterAccount.LoginUserName.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var rows = await query
            .OrderByDescending(x => x.TimeStamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = new List<ReviewListItem>();
        foreach (var row in rows)
        {
            items.Add(new ReviewListItem(
                row.ID,
                row.AssetId,
                row.Asset == null ? "未知资源" : row.Asset.Name,
                row.Asset == null ? "-" : row.Asset.Slug,
                row.Asset == null ? AssetType.Plugin : row.Asset.Type,
                row.Asset == null ? AssetStatus.Draft : row.Asset.Status,
                row.AssetVersion == null ? "未知版本" : row.AssetVersion.VersionName,
                row.SubmitterAccount == null ? "未知账号" : row.SubmitterAccount.LoginUserName,
                row.Status,
                row.Notes,
                row.Asset?.Type == AssetType.Solution ? ParseSolutionSummary(row.AssetVersion?.ManifestJson) : null,
                await GetRiskIssuesAsync(row, cancellationToken),
                row.ReviewerId,
                row.ReviewedAtUtc,
                row.TimeStamp));
        }

        var model = new ReviewIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            PendingCount = await dbContext.AssetReviews.CountAsync(x => x.Status == AssetReviewStatus.Pending, cancellationToken),
            ApprovedCount = await dbContext.AssetReviews.CountAsync(x => x.Status == AssetReviewStatus.Approved, cancellationToken),
            RejectedCount = await dbContext.AssetReviews.CountAsync(x => x.Status == AssetReviewStatus.Rejected, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        AssetReviewStatus? status,
        AssetType? assetType,
        AssetStatus? assetStatus,
        string? submitter,
        string? reviewer,
        DateTimeOffset? reviewedStart,
        DateTimeOffset? reviewedEnd,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.AssetReviews
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .Include(x => x.SubmitterAccount)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Notes.Contains(normalizedKeyword) ||
                (x.Asset != null && (x.Asset.Name.Contains(normalizedKeyword) || x.Asset.Slug.Contains(normalizedKeyword))) ||
                (x.SubmitterAccount != null && x.SubmitterAccount.LoginUserName.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (assetType is not null)
        {
            query = query.Where(x => x.Asset != null && x.Asset.Type == assetType);
        }

        if (assetStatus is not null)
        {
            query = query.Where(x => x.Asset != null && x.Asset.Status == assetStatus);
        }

        if (!string.IsNullOrWhiteSpace(submitter))
        {
            var normalizedSubmitter = submitter.Trim();
            query = query.Where(x => x.SubmitterAccount != null && x.SubmitterAccount.LoginUserName.Contains(normalizedSubmitter));
        }

        if (!string.IsNullOrWhiteSpace(reviewer))
        {
            var normalizedReviewer = reviewer.Trim();
            query = query.Where(x => x.ReviewerId != null && x.ReviewerId.Contains(normalizedReviewer));
        }

        if (reviewedStart is not null)
        {
            query = query.Where(x => x.ReviewedAtUtc >= reviewedStart);
        }

        if (reviewedEnd is not null)
        {
            query = query.Where(x => x.ReviewedAtUtc <= reviewedEnd);
        }

        var count = await query.CountAsync(cancellationToken);
        var rows = await ApplyReviewSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var data = new List<ReviewTableItem>(rows.Count);

        foreach (var row in rows)
        {
            var rowAssetType = row.Asset?.Type ?? AssetType.Plugin;
            var rowAssetStatus = row.Asset?.Status ?? AssetStatus.Draft;
            data.Add(new ReviewTableItem(
                row.ID,
                row.AssetId,
                row.Asset?.Name ?? "未知资源",
                row.Asset?.Slug ?? "-",
                rowAssetType,
                GetAssetTypeName(rowAssetType),
                rowAssetStatus,
                GetAssetStatusName(rowAssetStatus),
                GetAssetBadgeClass(rowAssetStatus),
                row.AssetVersion?.VersionName ?? "未知版本",
                row.SubmitterAccount?.LoginUserName ?? "未知账号",
                row.Status,
                GetReviewStatusName(row.Status),
                GetReviewBadgeClass(row.Status),
                row.Notes,
                row.Asset?.Type == AssetType.Solution ? ParseSolutionSummary(row.AssetVersion?.ManifestJson) : null,
                await GetRiskIssuesAsync(row, cancellationToken),
                row.ReviewerId ?? "-",
                row.ReviewedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
                row.TimeStamp));
        }

        return Json(new LayuiTableResult<ReviewTableItem>
        {
            Count = count,
            Data = data
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await reviewService.ApproveAsync(id, GetReviewerId(), notes ?? "审核通过", cancellationToken);
        TempData["AdminToast"] = ok ? "资源审核已通过并发布。" : await GetReviewFailureMessageAsync(id, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await reviewService.RejectAsync(id, GetReviewerId(), notes ?? "审核驳回", cancellationToken);
        TempData["AdminToast"] = ok ? "资源审核已驳回。" : "只有待审核记录可以驳回。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await reviewService.RevokeAsync(id, GetReviewerId(), notes ?? "审核撤回", cancellationToken);
        TempData["AdminToast"] = ok ? "资源审核已撤回，资源已下架。" : "只有已通过记录可以撤回。";
        return RedirectToAction(nameof(Index));
    }

    private static IQueryable<AssetReview> ApplyReviewSorting(IQueryable<AssetReview> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "assetName" => isAscending ? query.OrderBy(x => x.Asset!.Name) : query.OrderByDescending(x => x.Asset!.Name),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "reviewedAtText" => isAscending ? query.OrderBy(x => x.ReviewedAtUtc) : query.OrderByDescending(x => x.ReviewedAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.TimeStamp)
        };
    }

    private static string GetReviewStatusName(AssetReviewStatus status) => status switch
    {
        AssetReviewStatus.Pending => "待审核",
        AssetReviewStatus.Approved => "已通过",
        AssetReviewStatus.Rejected => "已驳回",
        AssetReviewStatus.Revoked => "已撤回",
        _ => status.ToString()
    };

    private static string GetReviewBadgeClass(AssetReviewStatus status) => status switch
    {
        AssetReviewStatus.Approved => "admin-badge-success",
        AssetReviewStatus.Rejected or AssetReviewStatus.Revoked => "admin-badge-danger",
        _ => "admin-badge-warning"
    };

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static string GetAssetStatusName(AssetStatus status) => status switch
    {
        AssetStatus.Draft => "草稿",
        AssetStatus.Published => "已发布",
        AssetStatus.Hidden => "隐藏",
        AssetStatus.Offline => "下架",
        _ => status.ToString()
    };

    private static string GetAssetBadgeClass(AssetStatus status) => status switch
    {
        AssetStatus.Published => "admin-badge-success",
        AssetStatus.Offline => "admin-badge-danger",
        AssetStatus.Hidden => "admin-badge-warning",
        _ => "admin-badge-muted"
    };

    private string GetReviewerId() => User.Identity?.Name ?? "admin";

    private async Task<IReadOnlyList<string>> GetRiskIssuesAsync(AssetReview review, CancellationToken cancellationToken)
    {
        if (review.Status == AssetReviewStatus.Rejected &&
            review.Notes.StartsWith("风控未通过：", StringComparison.Ordinal))
        {
            return review.Notes["风控未通过：".Length..]
                .Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (review.Status != AssetReviewStatus.Pending || review.AssetVersion is null)
        {
            return [];
        }

        return await GetPendingRiskIssuesAsync(review.AssetVersion, review.Asset?.Type ?? AssetType.Plugin, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetPendingRiskIssuesAsync(AssetVersion version, AssetType assetType, CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var metadataRisk = packageRiskService.AssessMetadata(version);
        issues.AddRange(metadataRisk.Issues);
        if (!metadataRisk.Passed)
        {
            return issues;
        }

        var integrity = await packageStorageService.VerifyPackageAsync(version, cancellationToken);
        if (integrity.Status != PackageIntegrityStatus.Verified)
        {
            issues.Add(GetPackageIntegrityIssue(integrity.Status));
            return issues;
        }

        try
        {
            using var packageStream = packageStorageService.OpenPackageRead(version);
            var archiveRisk = packageRiskService.AssessPackageArchive(packageStream, assetType);
            issues.AddRange(archiveRisk.Issues);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            issues.Add("资源包文件不存在或路径非法");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            issues.Add("资源包存储服务不可用或权限异常");
        }

        return issues;
    }

    private static string GetPackageIntegrityIssue(PackageIntegrityStatus status) => status switch
    {
        PackageIntegrityStatus.FileMissing => "资源包文件不存在或路径非法",
        PackageIntegrityStatus.HashMissing => "资源包缺少可信哈希",
        PackageIntegrityStatus.HashMismatch => "资源包哈希与记录不一致",
        PackageIntegrityStatus.ProviderMissing => "资源包存储提供方未注册",
        PackageIntegrityStatus.StorageUnavailable => "资源包存储服务不可用或权限异常",
        _ => "资源包完整性校验失败"
    };

    private static SolutionManifestSummary ParseSolutionSummary(string? manifestJson)
        => SolutionManifestParser.ParseManifestJson(manifestJson);

    private async Task<string> GetReviewFailureMessageAsync(string id, CancellationToken cancellationToken)
    {
        var review = await dbContext.AssetReviews.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        return review?.Status == AssetReviewStatus.Rejected && review.Notes.StartsWith("风控未通过", StringComparison.Ordinal)
            ? review.Notes
            : "审核通过失败，请确认记录仍为待审核状态。";
    }
}
