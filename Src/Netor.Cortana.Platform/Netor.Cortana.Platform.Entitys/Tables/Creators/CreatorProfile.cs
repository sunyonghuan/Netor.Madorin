using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;

namespace Netor.Cortana.Platform.Entitys.Tables.Creators;

/// <summary>
/// 创作者资料与准入状态。
/// </summary>
[Comment("创作者资料")]
public sealed class CreatorProfile : Base
{
    [StringLength(32)]
    [Comment("账号ID")]
    [Display(Name = "账号ID")]
    public string AccountId { get; set; } = string.Empty;

    public Account? Account { get; set; }

    [StringLength(128)]
    [Comment("创作者名称")]
    [Display(Name = "创作者名称")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(512)]
    [Comment("简介")]
    [Display(Name = "简介")]
    public string Bio { get; set; } = string.Empty;

    [Comment("状态")]
    [Display(Name = "状态")]
    public CreatorStatus Status { get; set; } = CreatorStatus.Pending;

    [Comment("通过时间")]
    [Display(Name = "通过时间")]
    public DateTimeOffset? ApprovedAtUtc { get; set; }
}
