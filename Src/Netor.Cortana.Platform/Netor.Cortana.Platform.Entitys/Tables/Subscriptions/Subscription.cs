using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Orders;

namespace Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

/// <summary>
/// 平台订阅记录。
/// </summary>
[Comment("平台订阅记录")]
public sealed class Subscription : Base
{
    public string AccountId { get; set; } = string.Empty;

    public Account? Account { get; set; }
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }
    public string PricingPlanId { get; set; } = string.Empty;

    public PricingPlan? PricingPlan { get; set; }
    public string? OrderId { get; set; }

    public Order? Order { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? CanceledAtUtc { get; set; }
}