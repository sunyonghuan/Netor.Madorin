using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum PlatformOrderStatus
{
    [Display(Name = "待支付")]
    Pending = 1,

    [Display(Name = "已支付")]
    Paid = 2,

    [Display(Name = "已取消")]
    Canceled = 3,

    [Display(Name = "支付失败")]
    Failed = 4
}