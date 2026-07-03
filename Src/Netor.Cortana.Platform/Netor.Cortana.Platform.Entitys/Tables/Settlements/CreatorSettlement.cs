using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Orders;

namespace Netor.Cortana.Platform.Entitys.Tables.Settlements;

/// <summary>
/// 创作者收益结算记录。
/// </summary>
[Comment("创作者收益结算记录")]
public sealed class CreatorSettlement : Base
{
    [StringLength(32)]
    [Comment("创作者账号ID")]
    [Display(Name = "创作者账号ID")]
    public string CreatorAccountId { get; set; } = string.Empty;

    public Account? CreatorAccount { get; set; }

    [StringLength(32)]
    [Comment("资源ID")]
    [Display(Name = "资源ID")]
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }

    [StringLength(32)]
    [Comment("订单ID")]
    [Display(Name = "订单ID")]
    public string OrderId { get; set; } = string.Empty;

    public Order? Order { get; set; }

    [Comment("订单金额")]
    [Display(Name = "订单金额")]
    public decimal GrossAmount { get; set; }

    [Comment("平台服务费")]
    [Display(Name = "平台服务费")]
    public decimal PlatformFeeAmount { get; set; }

    [Comment("创作者净收益")]
    [Display(Name = "创作者净收益")]
    public decimal NetAmount { get; set; }

    [StringLength(8)]
    [Comment("币种")]
    [Display(Name = "币种")]
    public string Currency { get; set; } = "CNY";

    [Comment("状态")]
    [Display(Name = "状态")]
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;

    [StringLength(512)]
    [Comment("备注")]
    [Display(Name = "备注")]
    public string Notes { get; set; } = string.Empty;

    [Comment("结算时间")]
    [Display(Name = "结算时间")]
    public DateTimeOffset? SettledAtUtc { get; set; }
}
