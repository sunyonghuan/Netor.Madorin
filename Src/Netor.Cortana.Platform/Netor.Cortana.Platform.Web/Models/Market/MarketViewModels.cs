using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Web.Models.Market;

public sealed class MarketIndexViewModel
{
    public IReadOnlyList<MarketCategoryViewModel> Categories { get; init; } = [];

    public IReadOnlyList<MarketAssetCardViewModel> FeaturedAssets { get; init; } = [];

    public IReadOnlyList<MarketAssetCardViewModel> LatestAssets { get; init; } = [];
}

public sealed class MarketAssetListViewModel
{
    public string? Keyword { get; init; }

    public AssetType? Type { get; init; }

    public string? CategoryId { get; init; }

    public string? Pricing { get; init; }

    public string? Sort { get; init; }

    public int Page { get; init; }

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public IReadOnlyList<MarketCategoryViewModel> Categories { get; init; } = [];

    public IReadOnlyList<MarketAssetCardViewModel> Items { get; init; } = [];
}

public sealed class MarketAssetDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string DeveloperName { get; init; } = string.Empty;

    public string ShortDescription { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public string? IconUrl { get; init; }

    public string? CoverUrl { get; init; }

    public AssetType Type { get; init; }

    public string TypeName { get; init; } = string.Empty;

    public string? CategoryName { get; init; }

    public int DownloadCount { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public IReadOnlyList<MarketAssetVersionViewModel> Versions { get; init; } = [];

    public IReadOnlyList<MarketPricingPlanViewModel> PricingPlans { get; init; } = [];
}

public sealed record MarketCategoryViewModel(string Id, string Name, string Slug, string? Description, int AssetCount);

public sealed record MarketAssetCardViewModel(
    string Id,
    string Name,
    string Slug,
    string DeveloperName,
    string ShortDescription,
    string? IconUrl,
    string? CoverUrl,
    AssetType Type,
    string TypeName,
    string? CategoryName,
    bool IsFeatured,
    int DownloadCount,
    long SortTime,
    decimal? LowestPrice,
    bool HasFreePlan,
    DateTimeOffset? PublishedAtUtc);

public sealed record MarketAssetVersionViewModel(string VersionName, string ReleaseNotes, long PackageSize, string FilePath);

public sealed record MarketPricingPlanViewModel(string Id, string Name, PricingPlanType PlanType, string PlanTypeName, decimal Price, string Currency, int DurationDays, bool IsActive);