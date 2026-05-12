using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

namespace Netor.Cortana.Platform.Web.Controllers;

[Authorize]
[Route("downloads")]
public sealed class DownloadsController(PlatformDbContext dbContext) : WebControllerBase
{
    [HttpGet("{assetId}")]
    public async Task<IActionResult> Download(string assetId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(x => x.ID == assetId && x.Status == AssetStatus.Published, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        var version = asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
        if (version is null)
        {
            TempData["WebToast"] = "该资源还没有可下载版本。";
            return RedirectToAction("Details", "Market", new { slug = asset.Slug });
        }

        var now = DateTimeOffset.UtcNow;
        var subscription = await dbContext.Subscriptions
            .Where(x => x.AccountId == accountId && x.AssetId == asset.ID && x.Status == SubscriptionStatus.Active)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null || subscription.ExpiresAtUtc < now)
        {
            var freePlan = asset.PricingPlans.FirstOrDefault(x => x.IsActive && (x.PlanType == PricingPlanType.Free || x.Price <= 0));
            if (freePlan is null)
            {
                TempData["WebToast"] = "请先订阅该资源后再下载。";
                return RedirectToAction("Details", "Market", new { slug = asset.Slug });
            }

            subscription = dbContext.Subscriptions.Add(new Subscription
            {
                AccountId = accountId,
                AssetId = asset.ID,
                PricingPlanId = freePlan.ID,
                Status = SubscriptionStatus.Active,
                StartedAtUtc = now,
                ExpiresAtUtc = freePlan.DurationDays <= 0 ? now.AddYears(20) : now.AddDays(freePlan.DurationDays)
            }).Entity;
        }

        dbContext.DownloadRecords.Add(new DownloadRecord
        {
            AccountId = accountId,
            AssetId = asset.ID,
            AssetVersionId = version.ID,
            SubscriptionId = subscription.ID,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        asset.DownloadCount++;
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["WebToast"] = $"已记录下载：{asset.Name} {version.VersionName}";
        return RedirectToAction("Downloads", "UserCenter");
    }
}