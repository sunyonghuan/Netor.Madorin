namespace Netor.Cortana.Platform.Admin.Models.Assets;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Netor.Cortana.Platform.Entitys.Enums;

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

public sealed record AssetCategoryOption(string Id, string Name);

public sealed record AssetVersionItem(string VersionName, string ReleaseNotes, long PackageSize, string FilePath);

public sealed record AssetPricingPlanItem(string Name, string PlanTypeName, decimal Price, string Currency, int DurationDays, bool IsActive);

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

    public string? CategoryName { get; init; }

    public bool IsFeatured { get; init; }

    public int DownloadCount { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public IReadOnlyList<AssetVersionItem> Versions { get; init; } = [];

    public IReadOnlyList<AssetPricingPlanItem> PricingPlans { get; init; } = [];
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

    public string Description { get; set; } = string.Empty;

    [StringLength(512)]
    public string Tags { get; set; } = string.Empty;

    public bool IsFeatured { get; set; }

    public bool PublishNow { get; set; } = true;

    [Required(ErrorMessage = "请输入版本号")]
    [StringLength(32)]
    public string VersionName { get; set; } = "1.0.0";

    public string? ReleaseNotes { get; set; }

    [Required(ErrorMessage = "请上传资源包")]
    public IFormFile? PackageFile { get; set; }

    [Required]
    public PricingPlanType PlanType { get; set; } = PricingPlanType.Free;

    [Range(0, 999999)]
    public decimal Price { get; set; }

    [StringLength(16)]
    public string Currency { get; set; } = "CNY";

    [Range(0, 3650)]
    public int DurationDays { get; set; }

    public IReadOnlyList<AssetCategoryOption> Categories { get; set; } = [];
}
