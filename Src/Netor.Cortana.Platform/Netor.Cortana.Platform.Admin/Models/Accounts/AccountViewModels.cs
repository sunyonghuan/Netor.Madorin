namespace Netor.Cortana.Platform.Admin.Models.Accounts;

using System.ComponentModel.DataAnnotations;

public sealed record AccountListItem(
    string Id,
    long No,
    string LoginUserName,
    string NickName,
    string Phone,
    string Email,
    byte Status,
    byte AccountType,
    int LoginTimes,
    DateTime LastLoginTime,
    decimal WalletBalance,
    int SubscriptionCount,
    int OrderCount);

public sealed record AccountTableItem(
    string Id,
    long No,
    string LoginUserName,
    string NickName,
    string RealName,
    string Phone,
    string Email,
    byte Status,
    string StatusName,
    string StatusBadgeClass,
    byte AccountType,
    string AccountTypeName,
    bool PhoneConfirmed,
    bool EmailConfirmed,
    int LoginTimes,
    string LastLoginTimeText,
    string LoginIp,
    string RegisterIp,
    decimal WalletBalance,
    int SubscriptionCount,
    int OrderCount,
    int DownloadCount,
    string CreatorStatus,
    long TimeStamp);

public sealed record AccountTransactionTableItem(
    string Id,
    string No,
    string OrderNo,
    string ThirdNo,
    string Title,
    string AssetName,
    string PricingPlanName,
    decimal Money,
    decimal RealMoney,
    byte Type,
    byte PayStatus,
    string PayStatusName,
    byte PayMethod,
    string PayMethodName,
    byte? OrderStatus,
    string OrderStatusName,
    string PayTimeText,
    long TimeStamp);

public sealed class AccountTransactionsViewModel
{
    public string AccountId { get; init; } = string.Empty;

    public string LoginUserName { get; init; } = string.Empty;
}

public sealed record AccountWalletListItem(
    string Name,
    decimal Money,
    byte Type,
    byte Status);

public sealed record AccountRoleListItem(
    string Name,
    int Power,
    bool Enabled);

public sealed record AccountPropertyListItem(
    string Key,
    string Value,
    string Display,
    string? Group);

public sealed class AccountIndexViewModel
{
    public IReadOnlyList<AccountListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public byte? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int EnabledCount { get; init; }

    public int DisabledCount { get; init; }

    public decimal WalletTotal { get; init; }

    public int SubscriptionCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class AccountDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public long No { get; init; }

    public string LoginUserName { get; init; } = string.Empty;

    public string NickName { get; init; } = string.Empty;

    public string RealName { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public bool PhoneConfirmed { get; init; }

    public bool EmailConfirmed { get; init; }

    public byte Status { get; init; }

    public byte AccountType { get; init; }

    public int LoginTimes { get; init; }

    public DateTime LastLoginTime { get; init; }

    public string LoginIP { get; init; } = string.Empty;

    public long TimeStamp { get; init; }

    public IReadOnlyList<AccountWalletListItem> Wallets { get; init; } = [];

    public IReadOnlyList<AccountRoleListItem> Roles { get; init; } = [];

    public IReadOnlyList<AccountPropertyListItem> Properties { get; init; } = [];

    public int SubscriptionCount { get; init; }

    public int OrderCount { get; init; }

    public int DownloadCount { get; init; }
}

public sealed class AccountEditViewModel
{
    public string Id { get; set; } = string.Empty;

    [Display(Name = "登录账号")]
    public string LoginUserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入昵称")]
    [StringLength(20, ErrorMessage = "昵称不能超过 20 个字符")]
    [Display(Name = "昵称")]
    public string NickName { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "真实姓名不能超过 50 个字符")]
    [Display(Name = "真实姓名")]
    public string? RealName { get; set; }

    [Required(ErrorMessage = "请输入手机号")]
    [StringLength(16, ErrorMessage = "手机号不能超过 16 个字符")]
    [Display(Name = "手机号码")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入邮箱")]
    [EmailAddress(ErrorMessage = "请输入有效邮箱")]
    [StringLength(126, ErrorMessage = "邮箱不能超过 126 个字符")]
    [Display(Name = "电子邮箱")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "手机已验证")]
    public bool PhoneConfirmed { get; set; }

    [Display(Name = "邮箱已验证")]
    public bool EmailConfirmed { get; set; }
}

public sealed class AccountStatusViewModel
{
    public string Id { get; set; } = string.Empty;

    public string LoginUserName { get; set; } = string.Empty;

    [Display(Name = "账户状态")]
    public byte Status { get; set; }
}

public sealed class AccountRechargeViewModel
{
    public string Id { get; set; } = string.Empty;

    public string LoginUserName { get; set; } = string.Empty;

    public decimal CurrentBalance { get; set; }

    public string RequestId { get; set; } = string.Empty;

    [Range(0.01, 999999, ErrorMessage = "充值金额必须大于 0")]
    [Display(Name = "充值金额")]
    public decimal Amount { get; set; }

    [StringLength(512, ErrorMessage = "充值原因不能超过 512 个字符")]
    [Display(Name = "充值原因")]
    public string? Reason { get; set; }
}

public sealed class AccountPasswordViewModel
{
    public string Id { get; set; } = string.Empty;

    public string LoginUserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入新登录密码")]
    [StringLength(64, MinimumLength = 6, ErrorMessage = "密码长度必须为 6-64 个字符")]
    [Display(Name = "新登录密码")]
    public string NewPassword { get; set; } = string.Empty;

    [StringLength(64, MinimumLength = 6, ErrorMessage = "安全密码长度必须为 6-64 个字符")]
    [Display(Name = "新安全密码")]
    public string? NewSafePassword { get; set; }
}
