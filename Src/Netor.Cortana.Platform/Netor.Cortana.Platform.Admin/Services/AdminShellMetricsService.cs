using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Services;

public sealed class AdminShellMetricsService(PlatformDbContext dbContext)
{
    private AdminShellMetrics? metrics;

    public async Task<AdminShellMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (metrics is not null)
        {
            return metrics;
        }

        var pendingOrderStatus = (byte)1;
        metrics = new AdminShellMetrics(
            PendingReviews: await dbContext.AssetReviews.AsNoTracking().CountAsync(x => x.Status == AssetReviewStatus.Pending, cancellationToken),
            PendingCreators: await dbContext.CreatorProfiles.AsNoTracking().CountAsync(x => x.Status == CreatorStatus.Pending, cancellationToken),
            PendingSettlements: await dbContext.CreatorSettlements.AsNoTracking().CountAsync(x => x.Status == SettlementStatus.Pending, cancellationToken),
            PendingSettlementAmount: await dbContext.CreatorSettlements.AsNoTracking().Where(x => x.Status == SettlementStatus.Pending).SumAsync(x => x.NetAmount, cancellationToken),
            PendingOrders: await dbContext.Orders.AsNoTracking().CountAsync(x => x.PayStatus == pendingOrderStatus, cancellationToken),
            ActiveSubscriptions: await dbContext.Subscriptions.AsNoTracking().CountAsync(x => x.Status == SubscriptionStatus.Active, cancellationToken),
            FeaturedAssets: await dbContext.Assets.AsNoTracking().CountAsync(x => x.IsFeatured, cancellationToken),
            PublishedAssets: await dbContext.Assets.AsNoTracking().CountAsync(x => x.Status == AssetStatus.Published, cancellationToken));

        return metrics;
    }
}

public sealed record AdminShellMetrics(
    int PendingReviews,
    int PendingCreators,
    int PendingSettlements,
    decimal PendingSettlementAmount,
    int PendingOrders,
    int ActiveSubscriptions,
    int FeaturedAssets,
    int PublishedAssets);
