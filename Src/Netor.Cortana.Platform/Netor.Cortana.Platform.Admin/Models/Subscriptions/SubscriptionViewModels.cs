using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Models.Subscriptions;

public sealed record SubscriptionListItem(
    string Id,
    string AccountName,
    string AssetName,
    string PricingPlanName,
    string StatusName,
    SubscriptionStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? CanceledAtUtc,
    string? OrderId,
    int DownloadCount);

public sealed record DownloadRecordListItem(
    string AccountName,
    string AssetName,
    string VersionName,
    string? IpAddress,
    string? UserAgent,
    long TimeStamp);

public sealed record SubscriptionTableItem(
    string Id,
    string AccountName,
    string AssetName,
    string PricingPlanName,
    string StatusName,
    SubscriptionStatus Status,
    string StatusBadgeClass,
    string StartedAtText,
    string ExpiresAtText,
    string CanceledAtText,
    int RemainingDays,
    string OrderId,
    int DownloadCount,
    long TimeStamp);

public sealed record DownloadTableItem(
    string Id,
    string AccountName,
    string AssetName,
    string VersionName,
    string SubscriptionId,
    string IpAddress,
    string UserAgent,
    long TimeStamp);

public sealed class SubscriptionIndexViewModel
{
    public IReadOnlyList<SubscriptionListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public SubscriptionStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int ActiveCount { get; init; }

    public int ExpiredCount { get; init; }

    public int CanceledCount { get; init; }

    public int DownloadCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class DownloadRecordIndexViewModel
{
    public IReadOnlyList<DownloadRecordListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}
