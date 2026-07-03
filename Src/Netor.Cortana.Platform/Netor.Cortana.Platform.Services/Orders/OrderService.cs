using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Cortana.Platform.Services.Settlements;
using Netor.Cortana.Platform.Services.Subscriptions;

namespace Netor.Cortana.Platform.Services.Orders;

public sealed class OrderService(
    PlatformDbContext dbContext,
    SubscriptionService subscriptionService,
    TimeProvider timeProvider,
    CreatorSettlementService? creatorSettlementService = null)
{
    public async Task<OrderPlanInfo?> GetPlanInfoAsync(string assetId, string pricingPlanId, CancellationToken cancellationToken = default)
    {
        return await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => x.ID == pricingPlanId && x.AssetId == assetId && x.IsActive && x.Asset != null && x.Asset.Status == AssetStatus.Published)
            .Select(x => new OrderPlanInfo(x.AssetId, x.ID, x.Asset!.Name, x.Name, x.Price, x.Currency, x.DurationDays))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CreateOrderResult> CreateAsync(string accountId, string assetId, string pricingPlanId, CancellationToken cancellationToken = default)
    {
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.ID == accountId, cancellationToken);
        if (account is null)
        {
            return CreateOrderResult.NotFound();
        }

        if (account.Status != 0)
        {
            return CreateOrderResult.AccountDisabled();
        }

        var plan = await dbContext.PricingPlans
            .Include(x => x.Asset)
            .FirstOrDefaultAsync(x => x.ID == pricingPlanId && x.AssetId == assetId && x.IsActive && x.Asset != null && x.Asset.Status == AssetStatus.Published, cancellationToken);

        if (plan?.Asset is null)
        {
            return CreateOrderResult.NotFound();
        }

        if (plan.Price <= 0)
        {
            await subscriptionService.EnsureActiveSubscriptionAsync(accountId, assetId, plan, cancellationToken: cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return CreateOrderResult.FreeSubscription(assetId);
        }

        var order = dbContext.Orders.Add(new Order
        {
            No = CreateOrderNo(),
            Title = $"订阅 {plan.Asset.Name} - {plan.Name}",
            Content = $"资源：{plan.Asset.Name}；方案：{plan.Name}",
            Money = plan.Price,
            Numbers = 1,
            PayStatus = 1,
            PayMethod = 0,
            Status = (byte)PlatformOrderStatus.Pending,
            AssetId = assetId,
            PricingPlanId = pricingPlanId,
            Account = account
        }).Entity;

        await dbContext.SaveChangesAsync(cancellationToken);
        return CreateOrderResult.PendingOrder(order.ID);
    }

    public async Task<OrderPaymentInfo?> GetPaymentInfoAsync(string accountId, string orderId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.ID == orderId && EF.Property<string>(x, "AccountID") == accountId)
            .Select(x => new OrderPaymentInfo(x.ID, x.No, x.Title, x.Money, x.PayStatus == 2))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PayOrderResult> PayAsync(string accountId, string orderId, CancellationToken cancellationToken = default)
    {
        var order = await dbContext.Orders
            .Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.ID == orderId && EF.Property<string>(x, "AccountID") == accountId, cancellationToken);

        if (order is null)
        {
            return PayOrderResult.NotFound();
        }

        if (order.Account is null || order.Account.Status != 0)
        {
            return PayOrderResult.AccountDisabled();
        }

        if (order.PayStatus == 2)
        {
            return PayOrderResult.Paid(order.ID);
        }

        if (string.IsNullOrWhiteSpace(order.AssetId) || string.IsNullOrWhiteSpace(order.PricingPlanId))
        {
            return PayOrderResult.NotFound();
        }

        var plan = await dbContext.PricingPlans
            .Include(x => x.Asset)
            .FirstOrDefaultAsync(x =>
                x.ID == order.PricingPlanId &&
                x.AssetId == order.AssetId &&
                x.IsActive &&
                x.Asset != null &&
                x.Asset.Status == AssetStatus.Published,
                cancellationToken);

        if (plan is null)
        {
            return PayOrderResult.NotFound();
        }

        var paidAt = timeProvider.GetUtcNow();
        order.PayStatus = 2;
        order.PayMethod = 9;
        order.Status = (byte)PlatformOrderStatus.Paid;
        order.PayTime = paidAt.LocalDateTime;

        await subscriptionService.RenewSubscriptionAsync(accountId, order.AssetId, plan, order.ID, cancellationToken);
        AddMockTransaction(order, paidAt.LocalDateTime);
        if (creatorSettlementService is not null && plan.Asset is not null)
        {
            await creatorSettlementService.CreatePendingSettlementAsync(order, plan.Asset, plan.Currency, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return PayOrderResult.Paid(order.ID);
    }

    private void AddMockTransaction(Order order, DateTime paidAt)
    {
        var exists = dbContext.Transactions.Any(x => x.OrderId == order.ID || x.OrderNo == order.No);
        if (exists)
        {
            return;
        }

        dbContext.Transactions.Add(new Transaction
        {
            OrderId = order.ID,
            Account = order.Account,
            Title = order.Title,
            No = $"TX{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}",
            ThirdNo = $"MOCK{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            OrderNo = order.No,
            Money = order.Money,
            RealMoney = order.Money,
            Numbers = 1,
            Content = order.Content,
            PayTime = paidAt,
            Type = 1,
            PayStatus = 2,
            PayMethod = 9
        });
    }

    private static string CreateOrderNo() => $"WEB{DateTime.UtcNow:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";
}

public sealed record OrderPlanInfo(string AssetId, string PricingPlanId, string AssetName, string PricingPlanName, decimal Price, string Currency, int DurationDays);

public sealed record OrderPaymentInfo(string OrderId, string OrderNo, string Title, decimal Amount, bool IsPaid);

public sealed record CreateOrderResult(CreateOrderStatus Status, string? OrderId, string? AssetId)
{
    public static CreateOrderResult NotFound() => new(CreateOrderStatus.NotFound, null, null);

    public static CreateOrderResult AccountDisabled() => new(CreateOrderStatus.AccountDisabled, null, null);

    public static CreateOrderResult FreeSubscription(string assetId) => new(CreateOrderStatus.FreeSubscriptionCreated, null, assetId);

    public static CreateOrderResult PendingOrder(string orderId) => new(CreateOrderStatus.PendingOrderCreated, orderId, null);
}

public enum CreateOrderStatus
{
    [Display(Name = "未找到")]
    NotFound,

    [Display(Name = "账号不可用")]
    AccountDisabled,

    [Display(Name = "免费订阅已创建")]
    FreeSubscriptionCreated,

    [Display(Name = "待支付订单已创建")]
    PendingOrderCreated
}

public sealed record PayOrderResult(bool Found, bool IsAccountDisabled, string? OrderId)
{
    public static PayOrderResult NotFound() => new(false, false, null);

    public static PayOrderResult AccountDisabled() => new(true, true, null);

    public static PayOrderResult Paid(string orderId) => new(true, false, orderId);
}
