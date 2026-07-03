using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Entitys.Tables.Assets;

/// <summary>
/// 平台资源定价方案。
/// </summary>
[Comment("平台资源定价方案")]
public sealed class PricingPlan : Base
{
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }
    [StringLength(64)]
    [Comment("名称")]
    [Display(Name = "名称")]
    public string Name { get; set; } = string.Empty;

    [Comment("方案类型")]
    [Display(Name = "方案类型")]
    public PricingPlanType PlanType { get; set; } = PricingPlanType.Free;

    [Comment("价格")]
    [Display(Name = "价格")]
    public decimal Price { get; set; }

    [StringLength(16)]
    [Comment("货币")]
    [Display(Name = "货币")]
    public string Currency { get; set; } = "CNY";

    [Comment("有效天数")]
    [Display(Name = "有效天数")]
    public int DurationDays { get; set; }

    [Comment("是否启用")]
    [Display(Name = "是否启用")]
    public bool IsActive { get; set; } = true;
}