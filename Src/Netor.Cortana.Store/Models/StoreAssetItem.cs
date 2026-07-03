using System.Text.Json;

namespace Netor.Cortana.Store.Models;

public sealed record StoreAssetItem(
    string Id,
    string Slug,
    AssetType Type,
    string Name,
    string DeveloperName,
    string ShortDescription,
    string? IconUrl,
    string? CoverUrl,
    string? CategoryName,
    bool IsFeatured,
    int DownloadCount,
    bool HasFreePlan,
    decimal? MinPrice,
    string? CurrentVersionId,
    string? CurrentVersionName);

public sealed record StoreAssetPage(
    IReadOnlyList<StoreAssetItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);

public sealed record StoreAssetDetail(
    string Id,
    string Slug,
    AssetType Type,
    string Name,
    string DeveloperName,
    string ShortDescription,
    string Description,
    string Tags,
    string? IconUrl,
    string? CoverUrl,
    string? CategoryName,
    int DownloadCount,
    StoreAssetVersion? CurrentVersion,
    IReadOnlyList<StorePricingPlan> PricingPlans);

public sealed record StoreAssetVersion(
    string Id,
    string VersionName,
    string ReleaseNotes,
    string PackageHash,
    long PackageSize,
    JsonElement? Manifest = null);

public sealed record StorePricingPlan(
    string Id,
    string Name,
    PricingPlanType PlanType,
    decimal Price,
    string Currency,
    int DurationDays);
