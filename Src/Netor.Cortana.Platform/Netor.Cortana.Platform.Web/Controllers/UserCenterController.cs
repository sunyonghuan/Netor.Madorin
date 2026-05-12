using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Web.Models.UserCenter;

namespace Netor.Cortana.Platform.Web.Controllers;

[Authorize]
[Route("user")]
public sealed class UserCenterController(PlatformDbContext dbContext) : WebControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);

        return View(new UserCenterIndexViewModel
        {
            DisplayName = account is null
                ? User.Identity?.Name ?? "个人用户"
                : string.IsNullOrWhiteSpace(account.NickName) ? account.LoginUserName : account.NickName,
            SubscriptionCount = await dbContext.Subscriptions.CountAsync(x => x.AccountId == accountId, cancellationToken),
            DownloadCount = await dbContext.DownloadRecords.CountAsync(x => x.AccountId == accountId, cancellationToken),
            OrderCount = await dbContext.Orders.CountAsync(x => EF.Property<string>(x, "AccountID") == accountId, cancellationToken),
            ExpiringSubscriptions = await GetSubscriptionItems(accountId, cancellationToken, onlyExpiringSoon: true)
        });
    }

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new UserProfileViewModel
        {
            LoginUserName = account.LoginUserName,
            Email = account.Email,
            Phone = account.Phone,
            NickName = account.NickName,
            RealName = account.RealName
        });
    }

    [HttpPost("profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(UserProfileViewModel model, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        account.NickName = model.NickName ?? string.Empty;
        account.RealName = model.RealName ?? string.Empty;
        account.Email = model.Email ?? string.Empty;
        account.Phone = model.Phone ?? string.Empty;

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["WebToast"] = "个人资料已保存。";
        return RedirectToAction(nameof(Profile));
    }

    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var items = await GetSubscriptionItems(accountId, cancellationToken);

        return View(new UserSubscriptionListViewModel { Items = items });
    }

    private async Task<IReadOnlyList<UserSubscriptionItem>> GetSubscriptionItems(string accountId, CancellationToken cancellationToken, bool onlyExpiringSoon = false)
    {
        var rows = await dbContext.Subscriptions
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.PricingPlan)
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(x =>
        {
            var remainingDays = (int)Math.Ceiling((x.ExpiresAtUtc - now).TotalDays);
            return new UserSubscriptionItem(
                x.ID,
                x.AssetId,
                x.PricingPlanId,
                x.Asset == null ? "未知资源" : x.Asset.Name,
                x.Asset == null ? "未知类型" : GetAssetTypeName(x.Asset.Type),
                x.PricingPlan == null ? "未知方案" : x.PricingPlan.Name,
                x.PricingPlan == null ? "未知周期" : GetPricingPlanTypeName(x.PricingPlan.PlanType),
                x.ExpiresAtUtc <= now ? "已过期" : GetSubscriptionStatusName(x.Status),
                x.StartedAtUtc,
                x.ExpiresAtUtc,
                remainingDays,
                x.Status == SubscriptionStatus.Active && remainingDays is >= 0 and <= 7,
                x.ExpiresAtUtc <= now);
        }).ToList();

        return onlyExpiringSoon
            ? items.Where(x => x.IsExpiringSoon || x.IsExpired).Take(5).ToList()
            : items;
    }

    [HttpPost("subscriptions/{id}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSubscription(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var subscription = await dbContext.Subscriptions.FirstOrDefaultAsync(x => x.ID == id && x.AccountId == accountId, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        if (subscription.Status == SubscriptionStatus.Active)
        {
            subscription.Status = SubscriptionStatus.Canceled;
            subscription.CanceledAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            TempData["WebToast"] = "订阅已取消。";
        }

        return RedirectToAction(nameof(Subscriptions));
    }

    [HttpGet("downloads")]
    public async Task<IActionResult> Downloads(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var items = await dbContext.DownloadRecords
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.ID)
            .Select(x => new UserDownloadItem(
                x.Asset == null ? "未知资源" : x.Asset.Name,
                x.AssetVersion == null ? "未知版本" : x.AssetVersion.VersionName,
                x.IpAddress,
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        return View(new UserDownloadListViewModel { Items = items });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId!;
        var items = await dbContext.Orders
            .AsNoTracking()
            .Where(x => EF.Property<string>(x, "AccountID") == accountId)
            .OrderByDescending(x => x.ID)
            .Select(x => new UserOrderItem(
                x.ID,
                x.No,
                x.Title,
                x.Money,
                x.PayStatus == 2 ? "已支付" : "待支付",
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        return View(new UserOrderListViewModel { Items = items });
    }

    private static string GetSubscriptionStatusName(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "有效",
        SubscriptionStatus.Expired => "已过期",
        SubscriptionStatus.Canceled => "已取消",
        _ => status.ToString()
    };

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static string GetPricingPlanTypeName(PricingPlanType type) => type switch
    {
        PricingPlanType.Free => "免费",
        PricingPlanType.Monthly => "月付",
        PricingPlanType.Yearly => "年付",
        _ => type.ToString()
    };
}