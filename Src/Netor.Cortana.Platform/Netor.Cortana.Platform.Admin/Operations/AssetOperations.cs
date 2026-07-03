using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Risk;

namespace Netor.Cortana.Platform.Admin.Operations;

public sealed class AssetOperations(
    PlatformDbContext dbContext,
    PackageStorageService packageStorageService,
    PackageRiskService packageRiskService,
    TimeProvider timeProvider)
{
    public async Task<OperationResult<AssetSearchResult>> SearchAsync(
        string? keyword,
        AssetType? type,
        AssetStatus? status,
        string? categoryId,
        bool? featured,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);

        var query = dbContext.Assets
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Name.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                x.DeveloperName.Contains(normalizedKeyword) ||
                x.ShortDescription.Contains(normalizedKeyword) ||
                x.Tags.Contains(normalizedKeyword));
        }

        if (type is not null)
        {
            if (!IsKnownAssetType(type.Value))
            {
                return OperationResult<AssetSearchResult>.Fail(OperationErrorCodes.Validation, "资源类型只能是 1=插件、2=技能、3=智能体、4=解决方案。");
            }

            query = query.Where(x => x.Type == type);
        }

        if (status is not null)
        {
            if (!IsKnownAssetStatus(status.Value))
            {
                return OperationResult<AssetSearchResult>.Fail(OperationErrorCodes.Validation, "资源状态只能是 1=草稿、2=已发布、3=隐藏、4=下架。");
            }

            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            var normalizedCategoryId = categoryId.Trim();
            query = query.Where(x => x.CategoryId == normalizedCategoryId);
        }

        if (featured is not null)
        {
            query = query.Where(x => x.IsFeatured == featured);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.TimeStamp)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.ID,
                x.Name,
                x.Slug,
                x.DeveloperName,
                OwnerAccountName = x.OwnerAccount == null ? null : x.OwnerAccount.LoginUserName,
                x.ShortDescription,
                x.Type,
                x.Status,
                CategoryName = x.Category == null ? null : x.Category.Name,
                x.IsFeatured,
                x.DownloadCount,
                VersionCount = x.Versions.Count,
                PricingPlanCount = x.PricingPlans.Count,
                x.PublishedAtUtc
            })
            .ToListAsync(cancellationToken);
        var items = rows
            .Select(x => new AssetSearchItem(
                x.ID,
                x.Name,
                x.Slug,
                x.DeveloperName,
                GetDisplayText(x.OwnerAccountName),
                GetDisplayText(x.ShortDescription),
                x.Type,
                GetAssetTypeName(x.Type),
                x.Status,
                GetAssetStatusName(x.Status),
                GetDisplayText(x.CategoryName),
                x.IsFeatured,
                x.DownloadCount,
                x.VersionCount,
                x.PricingPlanCount,
                x.PublishedAtUtc))
            .ToList();

        return OperationResult<AssetSearchResult>.Ok(new AssetSearchResult(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<AssetDetailResult>> GetAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return OperationResult<AssetDetailResult>.Fail(OperationErrorCodes.Validation, "资源ID不能为空。");
        }

        var asset = await dbContext.Assets
            .AsNoTracking()
            .Where(x => x.ID == assetId)
            .Select(x => new
            {
                x.ID,
                x.Name,
                x.Slug,
                x.DeveloperName,
                OwnerAccountName = x.OwnerAccount == null ? null : x.OwnerAccount.LoginUserName,
                x.ShortDescription,
                x.Description,
                x.Tags,
                x.Type,
                x.Status,
                CategoryName = x.Category == null ? null : x.Category.Name,
                x.CurrentVersionId,
                x.IsFeatured,
                x.DownloadCount,
                x.PublishedAtUtc,
                x.TimeStamp
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return OperationResult<AssetDetailResult>.Fail(OperationErrorCodes.NotFound, "资源不存在或已被删除。");
        }

        var versions = await dbContext.AssetVersions
            .AsNoTracking()
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .Select(x => new AssetVersionResult(
                x.ID,
                x.VersionName,
                GetDisplayText(x.ReleaseNotes),
                x.StorageProvider,
                x.PackageSize,
                !string.IsNullOrWhiteSpace(x.FilePath),
                x.TimeStamp))
            .ToListAsync(cancellationToken);
        var pricingPlans = await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => x.AssetId == asset.ID)
            .OrderBy(x => x.Price)
            .Select(x => new AssetPricingPlanResult(
                x.ID,
                x.Name,
                x.PlanType,
                GetPricingPlanTypeName(x.PlanType),
                x.Price,
                x.Currency,
                x.DurationDays,
                x.IsActive))
            .ToListAsync(cancellationToken);
        var latestReview = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .Select(x => new AssetReviewSummaryResult(
                x.ID,
                x.AssetVersionId,
                x.Status,
                GetReviewStatusName(x.Status),
                GetDisplayText(x.Notes),
                x.ReviewedAtUtc,
                x.TimeStamp))
            .FirstOrDefaultAsync(cancellationToken);

        return OperationResult<AssetDetailResult>.Ok(new AssetDetailResult(
            asset.ID,
            asset.Name,
            asset.Slug,
            asset.DeveloperName,
            GetDisplayText(asset.OwnerAccountName),
            GetDisplayText(asset.ShortDescription),
            GetDisplayText(asset.Description),
            GetDisplayText(asset.Tags),
            asset.Type,
            GetAssetTypeName(asset.Type),
            asset.Status,
            GetAssetStatusName(asset.Status),
            GetDisplayText(asset.CategoryName),
            asset.CurrentVersionId,
            asset.IsFeatured,
            asset.DownloadCount,
            asset.PublishedAtUtc,
            asset.TimeStamp,
            versions,
            pricingPlans,
            latestReview));
    }

    public async Task<OperationResult<AssetFeaturedResult>> SetFeaturedAsync(
        string assetId,
        bool featured,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return OperationResult<AssetFeaturedResult>.Fail(OperationErrorCodes.Validation, "资源ID不能为空。");
        }

        var asset = await dbContext.Assets.FirstOrDefaultAsync(x => x.ID == assetId, cancellationToken);
        if (asset is null)
        {
            return OperationResult<AssetFeaturedResult>.Fail(OperationErrorCodes.NotFound, "资源不存在或已被删除。");
        }

        asset.IsFeatured = featured;
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AssetFeaturedResult>.Ok(
            new AssetFeaturedResult(asset.ID, asset.IsFeatured),
            asset.IsFeatured ? "资源已设为推荐。" : "资源已取消推荐。");
    }

    public async Task<OperationResult<AssetStatusResult>> SetStatusAsync(
        string assetId,
        AssetStatus status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return OperationResult<AssetStatusResult>.Fail(OperationErrorCodes.Validation, "资源ID不能为空。");
        }

        if (!IsKnownAssetStatus(status))
        {
            return OperationResult<AssetStatusResult>.Fail(OperationErrorCodes.Validation, "资源状态只能是 1=草稿、2=已发布、3=隐藏、4=下架。");
        }

        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.ID == assetId, cancellationToken);
        if (asset is null)
        {
            return OperationResult<AssetStatusResult>.Fail(OperationErrorCodes.NotFound, "资源不存在或已被删除。");
        }

        if (status == AssetStatus.Published)
        {
            var publishValidation = await TryPreparePublishAsync(asset, cancellationToken);
            if (!publishValidation.Success)
            {
                return OperationResult<AssetStatusResult>.Fail(publishValidation.ErrorCode!, publishValidation.Message!);
            }
        }
        else
        {
            asset.Status = status;
            if (status != AssetStatus.Published)
            {
                asset.PublishedAtUtc = status == AssetStatus.Draft ? null : asset.PublishedAtUtc;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AssetStatusResult>.Ok(
            new AssetStatusResult(asset.ID, asset.Status, GetAssetStatusName(asset.Status), asset.PublishedAtUtc),
            $"资源状态已更新为 {GetAssetStatusName(asset.Status)}。");
    }

    private async Task<OperationResult<bool>> TryPreparePublishAsync(Asset asset, CancellationToken cancellationToken)
    {
        var version = GetCurrentVersion(asset);
        if (version is null)
        {
            return OperationResult<bool>.Fail(OperationErrorCodes.Validation, "资源没有可发布版本。");
        }

        var latestReview = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestReview is not null &&
            (latestReview.Status != AssetReviewStatus.Approved || latestReview.AssetVersionId != version.ID))
        {
            return OperationResult<bool>.Fail(OperationErrorCodes.Validation, "资源最新版本尚未审核通过。");
        }

        var metadataRisk = packageRiskService.AssessMetadata(version);
        if (!metadataRisk.Passed)
        {
            return OperationResult<bool>.Fail(OperationErrorCodes.Validation, $"资源包元数据未通过校验：{string.Join("；", metadataRisk.Issues)}");
        }

        var integrity = await packageStorageService.VerifyPackageAsync(version, cancellationToken);
        if (integrity.Status != PackageIntegrityStatus.Verified)
        {
            return OperationResult<bool>.Fail(OperationErrorCodes.Validation, $"资源包完整性校验失败：{GetPackageIntegrityStatusName(integrity.Status)}。");
        }

        try
        {
            using var packageStream = packageStorageService.OpenPackageRead(version);
            var archiveRisk = packageRiskService.AssessPackageArchive(packageStream, asset.Type);
            if (!archiveRisk.Passed)
            {
                return OperationResult<bool>.Fail(OperationErrorCodes.Validation, $"资源包风险校验失败：{string.Join("；", archiveRisk.Issues)}");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return OperationResult<bool>.Fail(OperationErrorCodes.Validation, $"资源包读取失败：{ex.Message}");
        }

        asset.Status = AssetStatus.Published;
        asset.CurrentVersionId ??= version.ID;
        asset.PublishedAtUtc ??= timeProvider.GetUtcNow();
        return OperationResult<bool>.Ok(true);
    }

    private static AssetVersion? GetCurrentVersion(Asset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.CurrentVersionId))
        {
            var current = asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId);
            if (current is not null)
            {
                return current;
            }
        }

        return asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
    }

    private static bool IsKnownAssetType(AssetType type)
        => type is AssetType.Plugin or AssetType.Skill or AssetType.Agent or AssetType.Solution;

    private static bool IsKnownAssetStatus(AssetStatus status)
        => status is AssetStatus.Draft or AssetStatus.Published or AssetStatus.Hidden or AssetStatus.Offline;

    private static string GetDisplayText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

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

    private static string GetReviewStatusName(AssetReviewStatus status) => status switch
    {
        AssetReviewStatus.Pending => "待审核",
        AssetReviewStatus.Approved => "已通过",
        AssetReviewStatus.Rejected => "已驳回",
        AssetReviewStatus.Revoked => "已撤回",
        _ => status.ToString()
    };

    private static string GetPricingPlanTypeName(PricingPlanType type) => type switch
    {
        PricingPlanType.Free => "免费",
        PricingPlanType.Monthly => "月度订阅",
        PricingPlanType.Yearly => "年度订阅",
        _ => type.ToString()
    };

    private static string GetPackageIntegrityStatusName(PackageIntegrityStatus status) => status switch
    {
        PackageIntegrityStatus.Verified => "已通过",
        PackageIntegrityStatus.FileMissing => "文件缺失",
        PackageIntegrityStatus.HashMissing => "缺少哈希",
        PackageIntegrityStatus.HashMismatch => "哈希不一致",
        PackageIntegrityStatus.ProviderMissing => "存储提供程序缺失",
        PackageIntegrityStatus.StorageUnavailable => "存储不可用",
        _ => status.ToString()
    };
}

public sealed record AssetSearchResult(
    IReadOnlyList<AssetSearchItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record AssetSearchItem(
    string Id,
    string Name,
    string Slug,
    string DeveloperName,
    string OwnerAccountName,
    string ShortDescription,
    AssetType Type,
    string TypeName,
    AssetStatus Status,
    string StatusName,
    string CategoryName,
    bool IsFeatured,
    int DownloadCount,
    int VersionCount,
    int PricingPlanCount,
    DateTimeOffset? PublishedAtUtc);

public sealed record AssetDetailResult(
    string Id,
    string Name,
    string Slug,
    string DeveloperName,
    string OwnerAccountName,
    string ShortDescription,
    string Description,
    string Tags,
    AssetType Type,
    string TypeName,
    AssetStatus Status,
    string StatusName,
    string CategoryName,
    string? CurrentVersionId,
    bool IsFeatured,
    int DownloadCount,
    DateTimeOffset? PublishedAtUtc,
    long TimeStamp,
    IReadOnlyList<AssetVersionResult> Versions,
    IReadOnlyList<AssetPricingPlanResult> PricingPlans,
    AssetReviewSummaryResult? LatestReview);

public sealed record AssetVersionResult(
    string Id,
    string VersionName,
    string ReleaseNotes,
    string StorageProvider,
    long PackageSize,
    bool HasPackage,
    long TimeStamp);

public sealed record AssetPricingPlanResult(
    string Id,
    string Name,
    PricingPlanType PlanType,
    string PlanTypeName,
    decimal Price,
    string Currency,
    int DurationDays,
    bool IsActive);

public sealed record AssetReviewSummaryResult(
    string Id,
    string AssetVersionId,
    AssetReviewStatus Status,
    string StatusName,
    string Notes,
    DateTimeOffset? ReviewedAtUtc,
    long TimeStamp);

public sealed record AssetFeaturedResult(string AssetId, bool IsFeatured);

public sealed record AssetStatusResult(
    string AssetId,
    AssetStatus Status,
    string StatusName,
    DateTimeOffset? PublishedAtUtc);
