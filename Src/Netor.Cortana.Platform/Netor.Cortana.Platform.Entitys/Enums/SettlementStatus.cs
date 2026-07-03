using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum SettlementStatus
{
    [Display(Name = "待结算")]
    Pending = 1,

    [Display(Name = "已结算")]
    Settled = 2,

    [Display(Name = "已冻结")]
    Frozen = 3,

    [Display(Name = "已驳回")]
    Rejected = 4
}
