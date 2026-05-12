using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Web.Models.Account;
using Netor.Extensions.EncryptExtensions;

namespace Netor.Cortana.Platform.Web.Controllers;

[Route("account")]
public sealed class AccountController(PlatformDbContext dbContext) : Controller
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

        var password = model.Password.MD5Encrypt();
        var account = await dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                (x.LoginUserName == model.UserName || x.Email == model.UserName || x.Phone == model.UserName) &&
                x.LoginPassword == password,
                cancellationToken);

        if (account is null)
        {
            ModelState.AddModelError(string.Empty, "账号或密码错误");
            return View(model);
        }

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