using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Models.Creators;

public sealed record CreatorListItem(
    string Id,
    string AccountId,
    string AccountName,
    string DisplayName,
    string Bio,
    CreatorStatus Status,
    DateTimeOffset? ApprovedAtUtc,
    int AssetCount,
    decimal PendingSettlementAmount);

public sealed record CreatorTableItem(
    string Id,
    string AccountId,
    string AccountName,
    string DisplayName,
    string Bio,
    CreatorStatus Status,
    string StatusName,
    string StatusBadgeClass,
    string ApprovedAtText,
    int AssetCount,
    int PendingReviewCount,
    decimal PendingSettlementAmount,
    decimal SettledSettlementAmount,
    long TimeStamp);

public sealed record CreatorAssetTableItem(
    string Id,
    string Name,
    string Slug,
    string TypeName,
    string StatusName,
    string CategoryName,
    int DownloadCount,
    int VersionCount,
    int PricingPlanCount,
    string PublishedAtText);

public sealed record CreatorSettlementTableItem(
    string Id,
    string AssetName,
    string OrderNo,
    decimal GrossAmount,
    decimal PlatformFeeAmount,
    decimal NetAmount,
    string Currency,
    string StatusName,
    string StatusBadgeClass,
    string SettledAtText,
    long TimeStamp);

public sealed class CreatorIndexViewModel
{
    public IReadOnlyList<CreatorListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public CreatorStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int PendingCount { get; init; }

    public int ApprovedCount { get; init; }

    public int SuspendedCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class CreatorDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Bio { get; init; } = string.Empty;

    public CreatorStatus Status { get; init; }

    public string StatusName { get; init; } = string.Empty;

    public string StatusBadgeClass { get; init; } = string.Empty;

    public DateTimeOffset? ApprovedAtUtc { get; init; }

    public int AssetCount { get; init; }

    public int PendingReviewCount { get; init; }

    public decimal PendingSettlementAmount { get; init; }

    public decimal SettledSettlementAmount { get; init; }
}

public sealed class CreatorRelatedViewModel
{
    public string CreatorId { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;
}
