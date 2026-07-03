using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum CreatorStatus
{
    [Display(Name = "待审核")]
    Pending = 1,

    [Display(Name = "已通过")]
    Approved = 2,

    [Display(Name = "已暂停")]
    Suspended = 3
}
