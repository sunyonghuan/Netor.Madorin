using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum AssetReviewStatus
{
    [Display(Name = "待审核")]
    Pending = 1,

    [Display(Name = "已通过")]
    Approved = 2,

    [Display(Name = "已驳回")]
    Rejected = 3,

    [Display(Name = "已撤回")]
    Revoked = 4
}
