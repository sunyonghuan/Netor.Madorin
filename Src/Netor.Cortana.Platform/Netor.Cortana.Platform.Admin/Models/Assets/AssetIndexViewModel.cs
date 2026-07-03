namespace Netor.Cortana.Platform.Admin.Models.Assets;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Services.Solutions;

public sealed record AssetListItem(
    string Id,
    string Name,
    string Slug,
    string DeveloperName,
    string? OwnerAccountName,
    string ShortDescription,
    AssetType Type,
    string TypeName,
    AssetStatus Status,
    string StatusName,
    string? CategoryName,
    bool IsFeatured,
    int DownloadCount,
    int VersionCount,
    int PricingPlanCount,
    DateTimeOffset? PublishedAtUtc);

public sealed record AssetTableItem(
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
    string StatusBadgeClass,
    string CategoryName,
    bool IsFeatured,
    int DownloadCount,
    int VersionCount,
    int PricingPlanCount,
    string CurrentVersionName,
    string PricingSummary,
    string ReviewStatusName,
    string PublishedAtText);

public sealed record AssetCategoryOption(string Id, string Name);

public sealed record AssetVersionItem(
    string Id,
    string VersionName,
    string ReleaseNotes,
    long PackageSize,
    string FilePath,
    bool IsCurrent,
    SolutionManifestSummary? SolutionSummary);

public sealed record AssetPricingPlanItem(string Name, string PlanTypeName, decimal Price, string Currency, int DurationDays, bool IsActive);

public sealed record AssetSubscriberItem(
    string AccountName,
    string PricingPlanName,
    string StatusName,
    string StatusBadgeClass,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int DownloadCount);

public sealed record AssetDownloadUserItem(
    string AccountName,
    string VersionName,
    string? IpAddress,
    DateTimeOffset DownloadedAtUtc);

public sealed record AssetBatchCategoryOption(string Id, string Name);

public sealed class AssetDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string DeveloperName { get; init; } = string.Empty;

    public string? OwnerAccountName { get; init; }

    public string ShortDescription { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public string TypeName { get; init; } = string.Empty;

    public string StatusName { get; init; } = string.Empty;

    public string StatusBadgeClass { get; init; } = string.Empty;

    public string? CategoryName { get; init; }

    public AssetStatus Status { get; init; }

    public bool IsFeatured { get; init; }

    public int DownloadCount { get; init; }

    public int TotalSubscriptionCount { get; init; }

    public int ActiveSubscriptionCount { get; init; }

    public int SubscriberUserCount { get; init; }

    public int VersionCount { get; init; }

    public int PricingPlanCount { get; init; }

    public string CurrentVersionName { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public SolutionManifestSummary? SolutionSummary { get; init; }

    public IReadOnlyList<AssetVersionItem> Versions { get; init; } = [];

    public IReadOnlyList<AssetPricingPlanItem> PricingPlans { get; init; } = [];

    public IReadOnlyList<AssetSubscriberItem> RecentSubscribers { get; init; } = [];

    public IReadOnlyList<AssetDownloadUserItem> RecentDownloads { get; init; } = [];
}

public sealed class AssetVersionUploadViewModel
{
    public string AssetId { get; set; } = string.Empty;

    public string AssetName { get; set; } = string.Empty;

    public string AssetSlug { get; set; } = string.Empty;

    public AssetType AssetType { get; set; }

    public string CurrentVersionName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入版本号")]
    [StringLength(32)]
    public string VersionName { get; set; } = string.Empty;

    public string? ReleaseNotes { get; set; }

    public string? UploadedPackageToken { get; set; }

    [StringLength(260)]
    public string? UploadedPackageFileName { get; set; }

    public long? UploadedPackageSize { get; set; }

    public string AssetTypeName => AssetType switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Solution => "解决方案",
        AssetType.Agent => "智能体",
        _ => AssetType.ToString()
    };

    public string ManifestFileName => AssetType switch
    {
        AssetType.Plugin => "plugin.json",
        AssetType.Skill => "skill.md",
        AssetType.Solution => "solution.json",
        AssetType.Agent => "agent.json",
        _ => "manifest.json"
    };
}

public sealed class AssetIndexViewModel
{
    public IReadOnlyList<AssetListItem> Items { get; init; } = [];

    public IReadOnlyList<AssetCategoryOption> Categories { get; init; } = [];

    public string? Keyword { get; init; }

    public AssetType? Type { get; init; }

    public AssetStatus? Status { get; init; }

    public string? CategoryId { get; init; }

    public bool? Featured { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int PublishedCount { get; init; }

    public int FeaturedCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class AssetCreateViewModel
{
    public string? Id { get; set; }

    [Required(ErrorMessage = "请输入资源名称")]
    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入资源标识")]
    [StringLength(160)]
    [RegularExpression("^[a-z0-9][a-z0-9-]*$", ErrorMessage = "资源标识只能包含小写字母、数字和连字符")]
    public string Slug { get; set; } = string.Empty;

    [Required]
    public AssetType Type { get; set; } = AssetType.Plugin;

    public string? CategoryId { get; set; }

    [Required(ErrorMessage = "请输入开发者名称")]
    [StringLength(128)]
    public string DeveloperName { get; set; } = "Netor 官方";

    [Required(ErrorMessage = "请输入简短描述")]
    [StringLength(256)]
    public string ShortDescription { get; set; } = string.Empty;

    public string? Description { get; set; }

    [StringLength(512)]
    public string? Tags { get; set; }

    public bool IsFeatured { get; set; }

    public bool PublishNow { get; set; } = true;

    [Required(ErrorMessage = "请输入版本号")]
    [StringLength(32)]
    public string VersionName { get; set; } = "1.0.0";

    public string? ReleaseNotes { get; set; }

    public IFormFile? PackageFile { get; set; }

    public string? UploadedPackageToken { get; set; }

    [StringLength(260)]
    public string? UploadedPackageFileName { get; set; }

    public long? UploadedPackageSize { get; set; }

    [Required]
    public PricingPlanType PlanType { get; set; } = PricingPlanType.Free;

    [Range(0, 999999)]
    public decimal Price { get; set; }

    [StringLength(16)]
    public string Currency { get; set; } = "CNY";

    [Range(0, 3650)]
    public int DurationDays { get; set; }

    public IReadOnlyList<AssetCategoryOption> Categories { get; set; } = [];

    public bool IsEdit => !string.IsNullOrWhiteSpace(Id);

    public string TypeName => Type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Solution => "解决方案",
        AssetType.Agent => "智能体",
        _ => Type.ToString()
    };

    public string ManifestFileName => Type switch
    {
        AssetType.Plugin => "plugin.json",
        AssetType.Skill => "skill.md",
        AssetType.Solution => "solution.json",
        AssetType.Agent => "agent.json",
        _ => "manifest.json"
    };

    public string ActiveAssetTab => Type switch
    {
        AssetType.Plugin => "plugin",
        AssetType.Skill => "skill",
        AssetType.Solution => "solution",
        AssetType.Agent => "agent",
        _ => "plugin"
    };
}
