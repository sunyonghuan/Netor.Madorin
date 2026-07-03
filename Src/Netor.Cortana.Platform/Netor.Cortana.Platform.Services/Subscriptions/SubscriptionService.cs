using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

namespace Netor.Cortana.Platform.Services.Subscriptions;

public sealed class SubscriptionService(PlatformDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<Subscription?> GetActiveSubscriptionAsync(string accountId, string assetId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var subscriptions = await dbContext.Subscriptions
            .Where(x => x.AccountId == accountId && x.AssetId == assetId && x.Status == SubscriptionStatus.Active)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .ToListAsync(cancellationToken);

        return subscriptions.FirstOrDefault(x => x.ExpiresAtUtc > now);
    }

    public async Task<Subscription> EnsureActiveSubscriptionAsync(
        string accountId,
        string assetId,
        PricingPlan pricingPlan,
        string? orderId = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetActiveSubscriptionAsync(accountId, assetId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        var subscription = dbContext.Subscriptions.Add(new Subscription
        {
            AccountId = accountId,
            AssetId = assetId,
            PricingPlanId = pricingPlan.ID,
            OrderId = orderId,
            Status = SubscriptionStatus.Active,
            StartedAtUtc = now,
            ExpiresAtUtc = pricingPlan.DurationDays <= 0 ? now.AddYears(20) : now.AddDays(pricingPlan.DurationDays)
        }).Entity;

        return subscription;
    }

    public async Task<Subscription> RenewSubscriptionAsync(
        string accountId,
        string assetId,
        PricingPlan pricingPlan,
        string orderId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var subscriptions = await dbContext.Subscriptions
            .Where(x => x.AccountId == accountId && x.AssetId == assetId && x.Status == SubscriptionStatus.Active)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .ToListAsync(cancellationToken);

        var existing = subscriptions.FirstOrDefault(x => x.ExpiresAtUtc > now);
        if (existing is not null)
        {
            var baseTime = existing.ExpiresAtUtc > now ? existing.ExpiresAtUtc : now;
            existing.PricingPlanId = pricingPlan.ID;
            existing.OrderId = orderId;
            existing.ExpiresAtUtc = pricingPlan.DurationDays <= 0 ? baseTime.AddYears(20) : baseTime.AddDays(pricingPlan.DurationDays);
            return existing;
        }

        var subscription = dbContext.Subscriptions.Add(new Subscription
        {
            AccountId = accountId,
            AssetId = assetId,
            PricingPlanId = pricingPlan.ID,
            OrderId = orderId,
            Status = SubscriptionStatus.Active,
            StartedAtUtc = now,
            ExpiresAtUtc = pricingPlan.DurationDays <= 0 ? now.AddYears(20) : now.AddDays(pricingPlan.DurationDays)
        }).Entity;

        return subscription;
    }

    public async Task<bool> CancelAsync(string accountId, string subscriptionId, CancellationToken cancellationToken = default)
    {
        var subscription = await dbContext.Subscriptions
            .FirstOrDefaultAsync(x => x.ID == subscriptionId && x.AccountId == accountId, cancellationToken);

        if (subscription is null)
        {
            return false;
        }

        if (subscription.Status == SubscriptionStatus.Active)
        {
            subscription.Status = SubscriptionStatus.Canceled;
            subscription.CanceledAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
