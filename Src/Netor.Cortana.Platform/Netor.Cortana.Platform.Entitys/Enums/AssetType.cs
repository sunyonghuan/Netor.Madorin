using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Platform.Entitys.Enums;

public enum AssetType
{
    [Display(Name = "插件")]
    Plugin = 1,

    [Display(Name = "技能")]
    Skill = 2,

    [Display(Name = "智能体")]
    Agent = 3,

    [Display(Name = "解决方案")]
    Solution = 4
}