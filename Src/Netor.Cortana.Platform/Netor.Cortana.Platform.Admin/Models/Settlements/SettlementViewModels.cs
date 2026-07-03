using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Models.Settlements;

public sealed record SettlementListItem(
    string Id,
    string CreatorAccountName,
    string AssetName,
    string OrderNo,
    decimal GrossAmount,
    decimal PlatformFeeAmount,
    decimal NetAmount,
    string Currency,
    SettlementStatus Status,
    string Notes,
    DateTimeOffset? SettledAtUtc,
    long TimeStamp);

public sealed record SettlementTableItem(
    string Id,
    string CreatorAccountName,
    string AssetName,
    string OrderNo,
    decimal GrossAmount,
    decimal PlatformFeeAmount,
    decimal NetAmount,
    string Currency,
    SettlementStatus Status,
    string StatusName,
    string StatusBadgeClass,
    string Notes,
    string SettledAtText,
    long TimeStamp);

public sealed class SettlementIndexViewModel
{
    public IReadOnlyList<SettlementListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public SettlementStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int PendingCount { get; init; }

    public int SettledCount { get; init; }

    public decimal PendingNetAmount { get; init; }

    public decimal SettledNetAmount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class SettlementDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string CreatorAccountName { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string OrderNo { get; init; } = string.Empty;

    public decimal GrossAmount { get; init; }

    public decimal PlatformFeeAmount { get; init; }

    public decimal NetAmount { get; init; }

    public string Currency { get; init; } = string.Empty;

    public SettlementStatus Status { get; init; }

    public string StatusName { get; init; } = string.Empty;

    public string StatusBadgeClass { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public DateTimeOffset? SettledAtUtc { get; init; }

    public long TimeStamp { get; init; }
}
