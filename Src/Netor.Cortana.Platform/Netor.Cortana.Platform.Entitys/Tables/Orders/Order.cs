using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;

namespace Netor.Cortana.Platform.Entitys.Tables.Orders;

/// <summary>
/// 平台销售单。
/// </summary>
[Comment("平台销售单")]
public sealed class Order : OrderBase<Account>
{
    [Comment("资产ID")]
    [Display(Name = "资产ID")]
    public string? AssetId { get; set; }

    [Comment("定价方案ID")]
    [Display(Name = "定价方案ID")]
    public string? PricingPlanId { get; set; }

    [Comment("详细")]
    [Display(Name = "详细")]
    public string Content { get; set; } = string.Empty;

    [NotMapped]
    public PlatformOrderStatus PlatformStatus { get; set; } = PlatformOrderStatus.Pending;
}