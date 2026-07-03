namespace Netor.Cortana.Platform.Entitys.Tables.Accounts;

/// <summary>
/// 平台个人用户角色。
/// </summary>
[Comment("平台个人用户角色")]
public sealed class AccountRole : RoleBase
{
    [Comment("排序")]
    [Display(Name = "排序")]
    public int Order { get; set; }
}