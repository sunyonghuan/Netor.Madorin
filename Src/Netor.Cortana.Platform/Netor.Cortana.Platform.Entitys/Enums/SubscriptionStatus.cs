using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum SubscriptionStatus
{
    [Display(Name = "有效")]
    Active = 1,

    [Display(Name = "已过期")]
    Expired = 2,

    [Display(Name = "已取消")]
    Canceled = 3
}