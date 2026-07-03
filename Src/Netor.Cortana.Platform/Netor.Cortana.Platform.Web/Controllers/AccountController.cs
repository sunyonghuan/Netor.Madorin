using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Services.Auth;
using Netor.Cortana.Platform.Web.Models.Account;
using Netor.Extensions.EncryptExtensions;

namespace Netor.Cortana.Platform.Web.Controllers;

[Route("account")]
public sealed class AccountController(PlatformDbContext dbContext, AuthService authService) : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var login = await authService.ValidateAccountAsync(model.UserName, model.Password, cancellationToken);

        if (login.Status == AccountLoginStatus.InvalidCredentials)
        {
            ModelState.AddModelError(string.Empty, "账号或密码错误");
            return View(model);
        }

        if (login.Status == AccountLoginStatus.AccountDisabled || login.Account is null)
        {
            ModelState.AddModelError(string.Empty, "账号已被禁用或冻结，请联系平台管理员");
            return View(model);
        }

        var account = login.Account;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.ID),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(account.NickName) ? account.LoginUserName : account.NickName),
            new("AccountNo", account.No.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            });

        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpGet("register")]
    public IActionResult Register()
    {
        return View(new RegisterViewModel());
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var exists = await dbContext.Accounts.AnyAsync(x => x.LoginUserName == model.UserName || x.Email == model.Email, cancellationToken);
        if (exists)
        {
            ModelState.AddModelError(string.Empty, "登录名或邮箱已存在");
            return View(model);
        }

        var account = dbContext.Accounts.Add(new Account
        {
            LoginUserName = model.UserName.Trim(),
            Email = model.Email.Trim(),
            Phone = model.Phone?.Trim() ?? string.Empty,
            LoginPassword = model.Password.MD5Encrypt(),
            SafePassword = model.Password.MD5Encrypt(),
            NickName = model.UserName.Trim()
        }).Entity;

        dbContext.AccountWallets.Add(new AccountWallet
        {
            Account = account
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["WebToast"] = "注册成功，请登录。";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("forgot-password")]
    public IActionResult ForgotPassword()
    {
        return View(new ForgotPasswordViewModel());
    }

    [HttpPost("forgot-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var normalized = model.Account.Trim();
        var accountExists = await dbContext.Accounts
            .AsNoTracking()
            .AnyAsync(x => x.LoginUserName == normalized || x.Email == normalized || x.Phone == normalized, cancellationToken);
        if (!accountExists)
        {
            ModelState.AddModelError(string.Empty, "没有找到匹配的账号。");
            return View(model);
        }

        return RedirectToAction(nameof(ResetPassword), new { account = normalized });
    }

    [HttpGet("reset-password")]
    public IActionResult ResetPassword(string? account = null)
    {
        return View(new ResetPasswordViewModel { Account = account ?? string.Empty });
    }

    [HttpPost("reset-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var normalizedAccount = model.Account.Trim();
        var normalizedEmail = model.Email.Trim();
        var account = await dbContext.Accounts.FirstOrDefaultAsync(
            x => (x.LoginUserName == normalizedAccount || x.Email == normalizedAccount || x.Phone == normalizedAccount) &&
                x.Email == normalizedEmail,
            cancellationToken);
        if (account is null)
        {
            ModelState.AddModelError(string.Empty, "账号和注册邮箱不匹配。");
            return View(model);
        }

        account.LoginPassword = model.Password.MD5Encrypt();
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["WebToast"] = "密码已重置，请使用新密码登录。";
        return RedirectToAction(nameof(Login));
    }

    [Authorize]
    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var accountId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new AccountProfileViewModel
        {
            LoginUserName = account.LoginUserName,
            NickName = account.NickName,
            Email = account.Email,
            Phone = account.Phone
        });
    }

    [Authorize]
    [HttpPost("profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(AccountProfileViewModel model, CancellationToken cancellationToken)
    {
        var accountId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        model.LoginUserName = account.LoginUserName;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var newPassword = model.NewPassword?.Trim();
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            if (string.IsNullOrWhiteSpace(model.CurrentPassword) ||
                account.LoginPassword != model.CurrentPassword.MD5Encrypt())
            {
                ModelState.AddModelError(nameof(AccountProfileViewModel.CurrentPassword), "当前密码不正确。");
                return View(model);
            }

            account.LoginPassword = newPassword.MD5Encrypt();
        }

        account.NickName = model.NickName?.Trim() ?? string.Empty;
        account.Email = model.Email.Trim();
        account.Phone = model.Phone?.Trim() ?? string.Empty;

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["WebToast"] = "账号资料已保存。";
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "UserCenter");
    }
}
