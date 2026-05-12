using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;
using Netor.Cortana.Platform.Web.Models.Orders;

namespace Netor.Cortana.Platform.Web.Controllers;

[Authorize]
[Route("orders")]
public sealed class OrdersController(PlatformDbContext dbContext) : WebControllerBase
{
    [HttpGet("confirm")]
    public async Task<IActionResult> Confirm(string assetId, string pricingPlanId, CancellationToken cancellationToken)
    {
        var data = await GetOrderDataAsync(assetId, pricingPlanId, cancellationToken);
        if (data is null)
        {
            return NotFound();
        }

        return View(new OrderConfirmViewModel
        {
            AssetId = assetId,
            PricingPlanId = pricingPlanId,
            AssetName = data.Value.AssetName,
            PricingPlanName = data.Value.PricingPlanName,
            Price = data.Value.Price,
            Currency = data.Value.Currency
        });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string assetId, string pricingPlanId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var data = await GetOrderDataAsync(assetId, pricingPlanId, cancellationToken);
        if (data is null)
        {
            return NotFound();
        }

        if (data.Value.Price <= 0)
        {
            var now = DateTimeOffset.UtcNow;
            var subscriptions = await dbContext.Subscriptions
                .Where(x => x.AccountId == accountId && x.AssetId == assetId && x.Status == SubscriptionStatus.Active)
                .ToListAsync(cancellationToken);
            var exists = subscriptions.Any(x => x.ExpiresAtUtc > now);
            if (!exists)
            {
                dbContext.Subscriptions.Add(new Subscription
                {
                    AccountId = accountId,
                    AssetId = assetId,
                    PricingPlanId = pricingPlanId,
                    Status = SubscriptionStatus.Active,
                    StartedAtUtc = now,
                    ExpiresAtUtc = data.Value.DurationDays <= 0 ? now.AddYears(20) : now.AddDays(data.Value.DurationDays)
                });

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            TempData["WebToast"] = "免费资源已加入订阅，可直接下载。";
            return RedirectToAction("Download", "Downloads", new { assetId });
        }

        var order = dbContext.Orders.Add(new Order
        {
            No = CreateOrderNo(),
            Title = $"订阅 {data.Value.AssetName} - {data.Value.PricingPlanName}",
            Content = $"资源：{data.Value.AssetName}；方案：{data.Value.PricingPlanName}",
            Money = data.Value.Price,
            Numbers = 1,
            PayStatus = 1,
            PayMethod = 0,
            Status = (byte)PlatformOrderStatus.Pending,
            AssetId = assetId,
            PricingPlanId = pricingPlanId,
            Account = await dbContext.Accounts.FirstAsync(x => x.ID == accountId, cancellationToken)
        }).Entity;

        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["WebToast"] = "订单已创建，请完成模拟支付。";
        return RedirectToAction(nameof(Pay), new { id = order.ID });
    }

    [HttpGet("pay/{id}")]
    public async Task<IActionResult> Pay(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        var order = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ID == id && EF.Property<string>(x, "AccountID") == accountId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        return View(new OrderPayViewModel { OrderId = id, OrderNo = order.No, Title = order.Title, Amount = order.Money });
    }

    [HttpPost("pay/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PayPost(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var order = await dbContext.Orders
            .FirstOrDefaultAsync(x => x.ID == id && EF.Property<string>(x, "AccountID") == accountId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        if (order.PayStatus != 2)
        {
            order.PayStatus = 2;
            order.PayMethod = 9;
            order.Status = (byte)PlatformOrderStatus.Paid;
            order.PayTime = DateTime.Now;

            var now = DateTimeOffset.UtcNow;
            var plan = await dbContext.PricingPlans.AsNoTracking().FirstOrDefaultAsync(x => x.ID == order.PricingPlanId, cancellationToken);
            if (plan is not null && !string.IsNullOrWhiteSpace(order.AssetId) && !string.IsNullOrWhiteSpace(order.PricingPlanId))
            {
                var activeSubscriptions = await dbContext.Subscriptions
                    .Where(x => x.AccountId == accountId && x.AssetId == order.AssetId && x.Status == SubscriptionStatus.Active)
                    .ToListAsync(cancellationToken);
                var hasActiveSubscription = activeSubscriptions.Any(x => x.ExpiresAtUtc > now);
                if (!hasActiveSubscription)
                {
                    dbContext.Subscriptions.Add(new Subscription
                    {
                        AccountId = accountId,
                        AssetId = order.AssetId,
                        PricingPlanId = order.PricingPlanId,
                        OrderId = order.ID,
                        Status = SubscriptionStatus.Active,
                        StartedAtUtc = now,
                        ExpiresAtUtc = plan.DurationDays <= 0 ? now.AddYears(20) : now.AddDays(plan.DurationDays)
                    });
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["WebToast"] = "模拟支付成功，订阅已生效。";
        return RedirectToAction(nameof(Result), new { id });
    }

    [HttpGet("result/{id}")]
    public async Task<IActionResult> Result(string id, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        var order = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ID == id && EF.Property<string>(x, "AccountID") == accountId, cancellationToken);

        if (order is null)
        {
            return NotFound();
        }

        return View(new OrderResultViewModel { OrderId = id, Title = order.Title, IsPaid = order.PayStatus == 2 });
    }

    private async Task<OrderData?> GetOrderDataAsync(string assetId, string pricingPlanId, CancellationToken cancellationToken)
    {
        return await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => x.ID == pricingPlanId && x.AssetId == assetId && x.IsActive && x.Asset != null && x.Asset.Status == AssetStatus.Published)
            .Select(x => new OrderData(x.Asset!.Name, x.Name, x.Price, x.Currency, x.DurationDays))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string CreateOrderNo() => $"WEB{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";

    private readonly record struct OrderData(string AssetName, string PricingPlanName, decimal Price, string Currency, int DurationDays);
}