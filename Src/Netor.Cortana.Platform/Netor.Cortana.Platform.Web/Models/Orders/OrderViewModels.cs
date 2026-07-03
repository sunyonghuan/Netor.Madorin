namespace Netor.Cortana.Platform.Web.Models.Orders;

public sealed class OrderConfirmViewModel
{
    public string AssetId { get; init; } = string.Empty;

    public string PricingPlanId { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public string PricingPlanName { get; init; } = string.Empty;

    public decimal Price { get; init; }

    public string Currency { get; init; } = "CNY";
}

public sealed class OrderPayViewModel
{
    public string OrderId { get; init; } = string.Empty;

    public string OrderNo { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public decimal Amount { get; init; }
}

public sealed class OrderResultViewModel
{
    public string OrderId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool IsPaid { get; init; }
}