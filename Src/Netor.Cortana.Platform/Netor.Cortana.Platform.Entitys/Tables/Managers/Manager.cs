namespace Netor.Cortana.Platform.Entitys.Tables.Managers;

/// <summary>
/// 平台管理员账户。
/// </summary>
[Comment("平台管理员账户")]
public sealed class Manager : AccountBase
{
    public ManagerRole? Role { get; set; }

    public ICollection<ManagerProperty> Properties { get; set; } = [];
}