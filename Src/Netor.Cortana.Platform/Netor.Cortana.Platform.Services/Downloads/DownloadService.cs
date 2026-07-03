using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Subscriptions;

namespace Netor.Cortana.Platform.Services.Downloads;

public sealed class DownloadService(
    PlatformDbContext dbContext,
    SubscriptionService subscriptionService,
    PackageStorageService packageStorageService)
{
    public async Task<DownloadPackageResult> PreparePackageAsync(
        string accountId,
        string assetIdOrSlug,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var accountEnabled = await dbContext.Accounts
            .AsNoTracking()
            .AnyAsync(x => x.ID == accountId && x.Status == 0, cancellationToken);

        if (!accountEnabled)
        {
            return DownloadPackageResult.AccountDisabled();
        }

        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(
                x => (x.ID == assetIdOrSlug || x.Slug == assetIdOrSlug) && x.Status == AssetStatus.Published,
                cancellationToken);

        if (asset is null)
        {
            return DownloadPackageResult.NotFound();
        }

        var version = string.IsNullOrWhiteSpace(asset.CurrentVersionId)
            ? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault()
            : asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId) ?? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
        if (version is null)
        {
            return DownloadPackageResult.NoVersion(asset.Slug);
        }

        var subscription = await subscriptionService.GetActiveSubscriptionAsync(accountId, asset.ID, cancellationToken);
        if (subscription is null)
        {
            var freePlan = asset.PricingPlans.FirstOrDefault(x => x.IsActive && (x.PlanType == PricingPlanType.Free || x.Price <= 0));
            if (freePlan is null)
            {
                return DownloadPackageResult.SubscriptionRequired(asset.Slug);
            }

            subscription = await subscriptionService.EnsureActiveSubscriptionAsync(accountId, asset.ID, freePlan, cancellationToken: cancellationToken);
        }

        var packageIntegrity = await packageStorageService.VerifyPackageAsync(version, cancellationToken);
        if (packageIntegrity.Status == PackageIntegrityStatus.FileMissing)
        {
            return DownloadPackageResult.FileMissing(asset.Slug, packageIntegrity.FullPath ?? version.FilePath);
        }

        if (packageIntegrity.Status != PackageIntegrityStatus.Verified)
        {
            return DownloadPackageResult.PackageIntegrityFailed(asset.Slug, packageIntegrity.FullPath ?? version.FilePath);
        }

        Stream stream;
        try
        {
            stream = packageStorageService.OpenPackageRead(version);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            return DownloadPackageResult.FileMissing(asset.Slug, packageIntegrity.FullPath ?? version.FilePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return DownloadPackageResult.PackageIntegrityFailed(asset.Slug, packageIntegrity.FullPath ?? version.FilePath);
        }

        dbContext.DownloadRecords.Add(new DownloadRecord
        {
            AccountId = accountId,
            AssetId = asset.ID,
            AssetVersionId = version.ID,
            SubscriptionId = subscription.ID,
            IpAddress = ipAddress,
            UserAgent = userAgent
        });

        asset.DownloadCount++;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        var fileName = Path.GetFileName(packageIntegrity.FullPath) ?? $"{asset.Slug}-{version.VersionName}.zip";
        return DownloadPackageResult.Ready(asset.Slug, asset.Name, version.VersionName, fileName, stream);
    }
}

public sealed record DownloadPackageResult(
    DownloadPackageStatus Status,
    string? AssetSlug,
    string? AssetName,
    string? VersionName,
    string? FileName,
    string? FullPath,
    Stream? Stream)
{
    public static DownloadPackageResult NotFound() => new(DownloadPackageStatus.NotFound, null, null, null, null, null, null);

    public static DownloadPackageResult AccountDisabled() => new(DownloadPackageStatus.AccountDisabled, null, null, null, null, null, null);

    public static DownloadPackageResult NoVersion(string assetSlug) => new(DownloadPackageStatus.NoVersion, assetSlug, null, null, null, null, null);

    public static DownloadPackageResult SubscriptionRequired(string assetSlug) => new(DownloadPackageStatus.SubscriptionRequired, assetSlug, null, null, null, null, null);

    public static DownloadPackageResult FileMissing(string assetSlug, string fullPath) => new(DownloadPackageStatus.FileMissing, assetSlug, null, null, null, fullPath, null);

    public static DownloadPackageResult PackageIntegrityFailed(string assetSlug, string fullPath) => new(DownloadPackageStatus.PackageIntegrityFailed, assetSlug, null, null, null, fullPath, null);

    public static DownloadPackageResult Ready(string assetSlug, string assetName, string versionName, string fileName, Stream stream)
        => new(DownloadPackageStatus.Ready, assetSlug, assetName, versionName, fileName, null, stream);
}

public enum DownloadPackageStatus
{
    [Display(Name = "可下载")]
    Ready,

    [Display(Name = "未找到")]
    NotFound,

    [Display(Name = "账号不可用")]
    AccountDisabled,

    [Display(Name = "无版本")]
    NoVersion,

    [Display(Name = "需要订阅")]
    SubscriptionRequired,

    [Display(Name = "文件缺失")]
    FileMissing,

    [Display(Name = "包校验失败")]
    PackageIntegrityFailed
}
