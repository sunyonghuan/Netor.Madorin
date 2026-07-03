using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Extensions.EncryptExtensions;

namespace Netor.Cortana.Platform.Services.Auth;

public sealed class AuthService(PlatformDbContext dbContext)
{
    public async Task<AccountLoginResult> ValidateAccountAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = userName.Trim();
        var passwordHash = password.MD5Encrypt();

        var account = await dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                (x.LoginUserName == normalizedUserName || x.Email == normalizedUserName || x.Phone == normalizedUserName) &&
                x.LoginPassword == passwordHash,
                cancellationToken);

        if (account is null)
        {
            return AccountLoginResult.InvalidCredentials();
        }

        if (account.Status != 0)
        {
            return AccountLoginResult.AccountDisabled(account);
        }

        return AccountLoginResult.Success(account);
    }
}

public sealed record AccountLoginResult(AccountLoginStatus Status, Account? Account)
{
    public static AccountLoginResult Success(Account account) => new(AccountLoginStatus.Success, account);

    public static AccountLoginResult InvalidCredentials() => new(AccountLoginStatus.InvalidCredentials, null);

    public static AccountLoginResult AccountDisabled(Account account) => new(AccountLoginStatus.AccountDisabled, account);
}

public enum AccountLoginStatus
{
    [Display(Name = "成功")]
    Success,

    [Display(Name = "账号或密码错误")]
    InvalidCredentials,

    [Display(Name = "账号不可用")]
    AccountDisabled
}
