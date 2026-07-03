using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Extensions.EncryptExtensions;

namespace Netor.Cortana.Platform.Admin.Operations;

public sealed class AccountOperations(PlatformDbContext dbContext, TimeProvider timeProvider)
{
    private const byte PaidStatus = 2;
    private const byte BalancePayMethod = 1;
    public const byte RechargeTransactionType = 9;
    private const byte DefaultWalletType = 1;
    private const byte EnabledStatus = 0;
    private const string DefaultWalletName = "余额钱包";
    private const string DefaultRechargeReason = "后台充值";

    public async Task<OperationResult<AccountProfileResult>> UpdateProfileAsync(
        string accountId,
        string nickName,
        string? realName,
        string phone,
        string email,
        bool phoneConfirmed,
        bool emailConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountProfileResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return OperationResult<AccountProfileResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        account.NickName = nickName.Trim();
        account.RealName = realName?.Trim() ?? string.Empty;
        account.Phone = phone.Trim();
        account.Email = email.Trim();
        account.PhoneConfirmed = phoneConfirmed;
        account.EmailConfirmed = emailConfirmed;
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AccountProfileResult>.Ok(new AccountProfileResult(account.ID), "用户基本信息已保存。");
    }

    public async Task<OperationResult<AccountStatusResult>> SetStatusAsync(
        string accountId,
        byte status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountStatusResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        if (!IsKnownAccountStatus(status))
        {
            return OperationResult<AccountStatusResult>.Fail(OperationErrorCodes.Validation, "账户状态只能是 0=正常、1=禁用、2=冻结。");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return OperationResult<AccountStatusResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        account.Status = status;
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AccountStatusResult>.Ok(new AccountStatusResult(account.ID, account.Status), "用户状态已更新。");
    }

    public async Task<OperationResult<BatchAccountStatusResult>> BatchSetStatusAsync(
        IReadOnlyCollection<string> accountIds,
        byte status,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = accountIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedIds.Length == 0)
        {
            return OperationResult<BatchAccountStatusResult>.Fail(OperationErrorCodes.Validation, "请先选择要操作的用户。");
        }

        if (!IsKnownAccountStatus(status))
        {
            return OperationResult<BatchAccountStatusResult>.Fail(OperationErrorCodes.Validation, "账户状态只能是 0=正常、1=禁用、2=冻结。");
        }

        var accounts = await dbContext.Accounts
            .Where(x => normalizedIds.Contains(x.ID))
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return OperationResult<BatchAccountStatusResult>.Fail(OperationErrorCodes.NotFound, "未找到可更新的用户。");
        }

        foreach (var account in accounts)
        {
            account.Status = status;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<BatchAccountStatusResult>.Ok(
            new BatchAccountStatusResult(accounts.Count, status),
            $"已批量更新 {accounts.Count} 个用户状态。");
    }

    public async Task<OperationResult<AccountRechargeResult>> RechargeWalletAsync(
        string managerId,
        string accountId,
        decimal amount,
        string source,
        string? requestId = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountRechargeResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        if (amount <= 0)
        {
            return OperationResult<AccountRechargeResult>.Fail(OperationErrorCodes.Validation, "充值金额必须大于 0。");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return OperationResult<AccountRechargeResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        var normalizedSource = NormalizeSource(source);
        var idempotencyKey = CreateRechargeIdempotencyKey(normalizedSource, requestId);
        if (idempotencyKey is not null)
        {
            var existingTransaction = await dbContext.Transactions
                .AsNoTracking()
                .Where(x =>
                    EF.Property<string>(x, "AccountID") == account.ID &&
                    x.Type == RechargeTransactionType &&
                    x.ThirdNo == idempotencyKey)
                .Select(x => new { x.ID, x.No, x.Money })
                .FirstOrDefaultAsync(cancellationToken);
            if (existingTransaction is not null)
            {
                var currentBalance = await GetWalletBalanceAsync(account.ID, cancellationToken);
                return OperationResult<AccountRechargeResult>.Ok(
                    new AccountRechargeResult(account.ID, currentBalance, existingTransaction.Money, existingTransaction.ID, existingTransaction.No),
                    "该充值请求已处理，已返回上次结果。");
            }
        }

        var wallet = await dbContext.AccountWallets
            .FirstOrDefaultAsync(x => EF.Property<string>(x, "AccountID") == account.ID, cancellationToken);
        if (wallet is null)
        {
            wallet = dbContext.AccountWallets.Add(new AccountWallet
            {
                Account = account,
                Name = DefaultWalletName,
                Type = DefaultWalletType,
                Status = EnabledStatus
            }).Entity;
        }

        wallet.Money += amount;

        var paidAt = timeProvider.GetUtcNow().LocalDateTime;
        var normalizedReason = NormalizeReason(reason);
        var transaction = dbContext.Transactions.Add(new Transaction
        {
            Account = account,
            OrderId = null,
            No = CreateTransactionNo(),
            ThirdNo = idempotencyKey ?? CreateThirdNo(normalizedSource),
            OrderNo = $"{normalizedSource}-RECHARGE",
            Title = "后台钱包充值",
            Content = BuildRechargeContent(managerId, normalizedSource, requestId, normalizedReason),
            Money = amount,
            RealMoney = amount,
            Numbers = 1,
            Type = RechargeTransactionType,
            PayStatus = PaidStatus,
            PayMethod = BalancePayMethod,
            PayTime = paidAt
        }).Entity;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AccountRechargeResult>.Ok(
            new AccountRechargeResult(account.ID, wallet.Money, amount, transaction.ID, transaction.No),
            $"已为用户充值 {amount:0.00}。");
    }

    public async Task<decimal> GetWalletBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        return await dbContext.AccountWallets
            .Where(x => EF.Property<string>(x, "AccountID") == accountId)
            .SumAsync(x => x.Money, cancellationToken);
    }

    public async Task<OperationResult<AccountSearchResult>> SearchAsync(
        string? keyword,
        byte? status,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);

        var query = dbContext.Accounts
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            var hasAccountNo = long.TryParse(normalizedKeyword, out var accountNo);
            query = query.Where(x =>
                (hasAccountNo && x.No == accountNo) ||
                x.LoginUserName.Contains(normalizedKeyword) ||
                x.Phone.Contains(normalizedKeyword) ||
                x.Email.Contains(normalizedKeyword) ||
                x.NickName.Contains(normalizedKeyword) ||
                x.RealName.Contains(normalizedKeyword));
        }

        if (status is not null)
        {
            if (!IsKnownAccountStatus(status.Value))
            {
                return OperationResult<AccountSearchResult>.Fail(OperationErrorCodes.Validation, "账户状态只能是 0=正常、1=禁用、2=冻结。");
            }

            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var accounts = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.ID,
                x.No,
                x.LoginUserName,
                x.NickName,
                x.Phone,
                x.Email,
                x.Status,
                x.AccountType,
                x.LoginTimes,
                x.LastLoginTime
            })
            .ToListAsync(cancellationToken);

        var accountIds = accounts.Select(x => x.ID).ToArray();
        var walletBalances = await dbContext.AccountWallets
            .AsNoTracking()
            .Where(x => accountIds.Contains(EF.Property<string>(x, "AccountID")))
            .GroupBy(x => EF.Property<string>(x, "AccountID"))
            .Select(x => new { AccountId = x.Key, Balance = x.Sum(w => w.Money) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance, cancellationToken);

        var items = accounts
            .Select(x => new AccountSearchItem(
                x.ID,
                x.No,
                x.LoginUserName,
                GetDisplayText(x.NickName),
                GetDisplayText(x.Phone),
                GetDisplayText(x.Email),
                x.Status,
                GetStatusName(x.Status),
                x.AccountType,
                GetAccountTypeName(x.AccountType),
                walletBalances.GetValueOrDefault(x.ID),
                x.LoginTimes,
                x.LastLoginTime == default ? null : x.LastLoginTime))
            .ToList();

        return OperationResult<AccountSearchResult>.Ok(new AccountSearchResult(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<AccountDetailResult>> GetAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountDetailResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        var account = await dbContext.Accounts
            .AsNoTracking()
            .Where(x => x.ID == accountId)
            .Select(x => new
            {
                x.ID,
                x.No,
                x.LoginUserName,
                x.NickName,
                x.RealName,
                x.Phone,
                x.Email,
                x.PhoneConfirmed,
                x.EmailConfirmed,
                x.Status,
                x.AccountType,
                x.LoginTimes,
                x.LastLoginTime,
                x.LoginIP,
                x.RegestorIP,
                x.TimeStamp
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return OperationResult<AccountDetailResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        var walletBalance = await GetWalletBalanceAsync(account.ID, cancellationToken);
        var subscriptionCount = await dbContext.Subscriptions.CountAsync(x => x.AccountId == account.ID, cancellationToken);
        var orderCount = await dbContext.Orders.CountAsync(x => EF.Property<string>(x, "AccountID") == account.ID, cancellationToken);
        var downloadCount = await dbContext.DownloadRecords.CountAsync(x => x.AccountId == account.ID, cancellationToken);

        return OperationResult<AccountDetailResult>.Ok(new AccountDetailResult(
            account.ID,
            account.No,
            account.LoginUserName,
            GetDisplayText(account.NickName),
            GetDisplayText(account.RealName),
            GetDisplayText(account.Phone),
            GetDisplayText(account.Email),
            account.PhoneConfirmed,
            account.EmailConfirmed,
            account.Status,
            GetStatusName(account.Status),
            account.AccountType,
            GetAccountTypeName(account.AccountType),
            account.LoginTimes,
            account.LastLoginTime == default ? null : account.LastLoginTime,
            GetDisplayText(account.LoginIP),
            GetDisplayText(account.RegestorIP),
            walletBalance,
            subscriptionCount,
            orderCount,
            downloadCount,
            account.TimeStamp));
    }

    public async Task<OperationResult<AccountTransactionSearchResult>> ListTransactionsAsync(
        string accountId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountTransactionSearchResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        var exists = await dbContext.Accounts.AsNoTracking().AnyAsync(x => x.ID == accountId, cancellationToken);
        if (!exists)
        {
            return OperationResult<AccountTransactionSearchResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 20);

        var query = dbContext.Transactions
            .AsNoTracking()
            .Where(x => EF.Property<string>(x, "AccountID") == accountId);
        var totalCount = await query.CountAsync(cancellationToken);
        var transactions = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.ID,
                x.No,
                x.OrderNo,
                x.ThirdNo,
                x.Title,
                x.Money,
                x.RealMoney,
                x.Type,
                x.PayStatus,
                x.PayMethod,
                x.PayTime,
                x.TimeStamp
            })
            .ToListAsync(cancellationToken);
        var items = transactions
            .Select(x => new AccountTransactionSearchItem(
                x.ID,
                GetDisplayText(x.No),
                GetDisplayText(x.OrderNo),
                GetDisplayText(x.ThirdNo),
                GetDisplayText(x.Title),
                x.Money,
                x.RealMoney,
                x.Type,
                GetTransactionTypeName(x.Type),
                x.PayStatus,
                GetPayStatusName(x.PayStatus),
                x.PayMethod,
                GetPayMethodName(x.PayMethod),
                x.PayTime,
                x.TimeStamp))
            .ToList();

        return OperationResult<AccountTransactionSearchResult>.Ok(new AccountTransactionSearchResult(items, page, pageSize, totalCount));
    }

    public async Task<OperationResult<AccountPasswordResult>> ResetPasswordAsync(
        string accountId,
        string newPassword,
        string? newSafePassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return OperationResult<AccountPasswordResult>.Fail(OperationErrorCodes.Validation, "用户ID不能为空。");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return OperationResult<AccountPasswordResult>.Fail(OperationErrorCodes.NotFound, "用户不存在或已被删除。");
        }

        account.LoginPassword = newPassword.MD5Encrypt();
        if (!string.IsNullOrWhiteSpace(newSafePassword))
        {
            account.SafePassword = newSafePassword.MD5Encrypt();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AccountPasswordResult>.Ok(new AccountPasswordResult(account.ID), "用户密码已重置。");
    }

    private static string NormalizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "MVC";
        }

        var normalized = source.Trim().ToUpperInvariant();
        return normalized.Length <= 16 ? normalized : normalized[..16];
    }

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? DefaultRechargeReason : reason.Trim();

    private static string? CreateRechargeIdempotencyKey(string source, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var key = $"{source}-RECHARGE-{requestId.Trim()}";
        return NormalizeText(key, 128);
    }

    private static string BuildRechargeContent(string managerId, string source, string? requestId, string reason)
    {
        var content = $"来源：{source}；操作人：{NormalizeText(managerId, 64)}；原因：{NormalizeText(reason, 512)}";
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            content += $"；请求ID：{NormalizeText(requestId, 128)}";
        }

        return NormalizeText(content, 2096);
    }

    private static string NormalizeText(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string CreateTransactionNo()
        => $"TX{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";

    private static string CreateThirdNo(string source)
        => $"{source}{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";

    private static bool IsKnownAccountStatus(byte status)
        => status is 0 or 1 or 2;

    private static string GetDisplayText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string GetStatusName(byte status) => status switch
    {
        0 => "正常",
        1 => "禁用",
        2 => "冻结",
        _ => $"状态 {status}"
    };

    private static string GetAccountTypeName(byte type) => type switch
    {
        0 => "普通用户",
        1 => "个人用户",
        2 => "企业用户",
        _ => $"类型 {type}"
    };

    private static string GetPayStatusName(byte status) => status switch
    {
        0 => "未知",
        1 => "待支付",
        2 => "已支付",
        3 => "已取消",
        4 => "失败",
        _ => $"状态 {status}"
    };

    private static string GetPayMethodName(byte method) => method switch
    {
        0 => "未知",
        1 => "余额",
        2 => "微信",
        3 => "支付宝",
        4 => "银行卡",
        _ => $"方式 {method}"
    };

    private static string GetTransactionTypeName(byte type) => type switch
    {
        1 => "收入",
        2 => "支出",
        3 => "退款",
        RechargeTransactionType => "钱包充值",
        _ => type == 0 ? "未知" : $"类型 {type}"
    };
}

public sealed record AccountStatusResult(string AccountId, byte Status);

public sealed record AccountProfileResult(string AccountId);

public sealed record BatchAccountStatusResult(int UpdatedCount, byte Status);

public sealed record AccountRechargeResult(
    string AccountId,
    decimal CurrentBalance,
    decimal Amount,
    string TransactionId,
    string TransactionNo);

public sealed record AccountPasswordResult(string AccountId);

public sealed record AccountSearchResult(
    IReadOnlyList<AccountSearchItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record AccountSearchItem(
    string Id,
    long No,
    string LoginUserName,
    string NickName,
    string Phone,
    string Email,
    byte Status,
    string StatusName,
    byte AccountType,
    string AccountTypeName,
    decimal WalletBalance,
    int LoginTimes,
    DateTime? LastLoginTime);

public sealed record AccountDetailResult(
    string Id,
    long No,
    string LoginUserName,
    string NickName,
    string RealName,
    string Phone,
    string Email,
    bool PhoneConfirmed,
    bool EmailConfirmed,
    byte Status,
    string StatusName,
    byte AccountType,
    string AccountTypeName,
    int LoginTimes,
    DateTime? LastLoginTime,
    string LoginIp,
    string RegisterIp,
    decimal WalletBalance,
    int SubscriptionCount,
    int OrderCount,
    int DownloadCount,
    long TimeStamp);

public sealed record AccountTransactionSearchResult(
    IReadOnlyList<AccountTransactionSearchItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record AccountTransactionSearchItem(
    string Id,
    string No,
    string OrderNo,
    string ThirdNo,
    string Title,
    decimal Money,
    decimal RealMoney,
    byte Type,
    string TypeName,
    byte PayStatus,
    string PayStatusName,
    byte PayMethod,
    string PayMethodName,
    DateTime? PayTime,
    long TimeStamp);
