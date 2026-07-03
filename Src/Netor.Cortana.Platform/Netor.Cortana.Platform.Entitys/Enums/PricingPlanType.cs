using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum PricingPlanType
{
    [Display(Name = "免费")]
    Free = 1,

    [Display(Name = "月度订阅")]
    Monthly = 2,

    [Display(Name = "年度订阅")]
    Yearly = 3
}