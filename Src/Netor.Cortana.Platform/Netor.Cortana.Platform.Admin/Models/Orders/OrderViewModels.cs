namespace Netor.Cortana.Platform.Admin.Models.Orders;

public sealed record OrderListItem(
    string Id,
    string No,
    string Title,
    string AccountName,
    string AssetName,
    string PricingPlanName,
    decimal Money,
    int Numbers,
    byte PayStatus,
    byte PayMethod,
    byte Status,
    DateTime? PayTime,
    long TimeStamp,
    int TransactionCount);

public sealed record TransactionListItem(
    string Id,
    string No,
    string OrderNo,
    string ThirdNo,
    string AccountName,
    string Title,
    decimal Money,
    decimal RealMoney,
    byte Type,
    byte PayStatus,
    byte PayMethod,
    DateTime? PayTime,
    long TimeStamp);

public sealed record OrderTableItem(
    string Id,
    string No,
    string Title,
    string AccountName,
    string AssetName,
    string PricingPlanName,
    string ContentSummary,
    decimal Money,
    int Numbers,
    byte PayStatus,
    string PayStatusName,
    string PayStatusBadgeClass,
    byte PayMethod,
    string PayMethodName,
    byte Status,
    string StatusName,
    string StatusBadgeClass,
    string PayTimeText,
    long TimeStamp,
    int TransactionCount);

public sealed record TransactionTableItem(
    string Id,
    string No,
    string OrderNo,
    string ThirdNo,
    string AccountName,
    string Title,
    decimal Money,
    decimal RealMoney,
    byte Type,
    string TypeName,
    byte PayStatus,
    string PayStatusName,
    string PayStatusBadgeClass,
    byte PayMethod,
    string PayMethodName,
    string PayTimeText,
    long TimeStamp);

public sealed class OrderIndexViewModel
{
    public IReadOnlyList<OrderListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public byte? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int PaidCount { get; init; }

    public int PendingCount { get; init; }

    public decimal PaidAmount { get; init; }

    public int TransactionCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class OrderDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string No { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string PricingPlanName { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public decimal Money { get; init; }

    public int Numbers { get; init; }

    public byte PayStatus { get; init; }

    public byte PayMethod { get; init; }

    public byte Status { get; init; }

    public DateTime? PayTime { get; init; }

    public long TimeStamp { get; init; }

    public IReadOnlyList<TransactionListItem> Transactions { get; init; } = [];
}

public sealed class TransactionIndexViewModel
{
    public IReadOnlyList<TransactionListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public byte? PayStatus { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}
