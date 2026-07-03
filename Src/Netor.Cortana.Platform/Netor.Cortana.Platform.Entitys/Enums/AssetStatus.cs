using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum AssetStatus
{
    [Display(Name = "草稿")]
    Draft = 1,

    [Display(Name = "已发布")]
    Published = 2,

    [Display(Name = "隐藏")]
    Hidden = 3,

    [Display(Name = "下架")]
    Offline = 4
}