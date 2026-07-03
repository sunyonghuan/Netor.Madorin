using Netor.Cortana.Platform.Entitys.Tables.Accounts;

namespace Netor.Cortana.Platform.Entitys.Tables.Orders;

/// <summary>
/// 平台交易记录。
/// </summary>
[Comment("平台交易记录")]
public sealed class Transaction : TransactionBase<Account>
{
    [Comment("关联销售单ID")]
    [Display(Name = "关联销售单ID")]
    public string? OrderId { get; set; }
}