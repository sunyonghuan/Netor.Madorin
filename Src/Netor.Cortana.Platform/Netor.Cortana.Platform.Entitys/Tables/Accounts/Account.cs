namespace Netor.Cortana.Platform.Entitys.Tables.Accounts;

/// <summary>
/// 平台个人用户账户。
/// </summary>
[Comment("平台个人用户账户")]
public sealed class Account : AccountBase
{
    public ICollection<AccountWallet> Wallets { get; set; } = [];

    public ICollection<AccountRolePair> RolePairs { get; set; } = [];

    public ICollection<AccountProperty> Properties { get; set; } = [];
}