using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Operations;

public sealed class DashboardOperations(
    PlatformDbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private const byte PendingPayStatus = 1;
    private const byte PaidPayStatus = 2;
    private const byte EnabledAccountStatus = 0;
    private const byte DisabledAccountStatus = 1;
    private const byte FrozenAccountStatus = 2;

    public async Task<OperationResult<DashboardSummaryResult>> GetSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var trendStart = today.AddDays(-6);

        var accounts = await GetAccountsSummaryAsync(cancellationToken);
        var assets = await GetAssetsSummaryAsync(cancellationToken);
        var orders = await GetOrdersSummaryAsync(today, tomorrow, cancellationToken);
        var wallets = await GetWalletsSummaryAsync(today, tomorrow, cancellationToken);
        var subscriptions = await GetSubscriptionsSummaryAsync(nowUtc, cancellationToken);
        var operations = await GetOperationsSummaryAsync(cancellationToken);
        var trend = await GetTrendAsync(trendStart, tomorrow, cancellationToken);
        var hotAssets = await GetHotAssetsAsync(cancellationToken);

        var result = new DashboardSummaryResult(
            GeneratedAtUtc: nowUtc,
            Accounts: accounts,
            Assets: assets,
            Orders: orders,
            Wallets: wallets,
            Subscriptions: subscriptions,
            Operations: operations,
            Trend: trend,
            HotAssets: hotAssets,
            Runtime: GetRuntimeSummary(),
            Mcp: GetMcpSummary());

        return OperationResult<DashboardSummaryResult>.Ok(result);
    }

    private async Task<DashboardAccountsSummary> GetAccountsSummaryAsync(CancellationToken cancellationToken)
    {
        var statusRows = await dbContext.Accounts
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() })
            .ToListAsync(cancellationToken);

        return new DashboardAccountsSummary(
            TotalCount: statusRows.Sum(x => x.Count),
            EnabledCount: statusRows.Where(x => x.Status == EnabledAccountStatus).Sum(x => x.Count),
            DisabledCount: statusRows.Where(x => x.Status == DisabledAccountStatus).Sum(x => x.Count),
            FrozenCount: statusRows.Where(x => x.Status == FrozenAccountStatus).Sum(x => x.Count));
    }

    private async Task<DashboardAssetsSummary> GetAssetsSummaryAsync(CancellationToken cancellationToken)
    {
        var statusRows = await dbContext.Assets
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() })
            .ToListAsync(cancellationToken);
        var featuredCount = await dbContext.Assets
            .AsNoTracking()
            .CountAsync(x => x.IsFeatured, cancellationToken);
        var pendingReviewCount = await dbContext.AssetReviews
            .AsNoTracking()
            .CountAsync(x => x.Status == AssetReviewStatus.Pending, cancellationToken);

        return new DashboardAssetsSummary(
            TotalCount: statusRows.Sum(x => x.Count),
            PublishedCount: statusRows.Where(x => x.Status == AssetStatus.Published).Sum(x => x.Count),
            DraftCount: statusRows.Where(x => x.Status == AssetStatus.Draft).Sum(x => x.Count),
            HiddenCount: statusRows.Where(x => x.Status == AssetStatus.Hidden).Sum(x => x.Count),
            OfflineCount: statusRows.Where(x => x.Status == AssetStatus.Offline).Sum(x => x.Count),
            FeaturedCount: featuredCount,
            PendingReviewCount: pendingReviewCount);
    }

    private async Task<DashboardOrdersSummary> GetOrdersSummaryAsync(
        DateTime today,
        DateTime tomorrow,
        CancellationToken cancellationToken)
    {
        var todayCount = await dbContext.Orders
            .AsNoTracking()
            .CountAsync(x => x.PayTime >= today && x.PayTime < tomorrow, cancellationToken);
        var todayPaidCount = await dbContext.Orders
            .AsNoTracking()
            .CountAsync(x => x.PayStatus == PaidPayStatus && x.PayTime >= today && x.PayTime < tomorrow, cancellationToken);
        var todayIncome = await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.PayStatus == PaidPayStatus && x.PayTime >= today && x.PayTime < tomorrow)
            .Select(x => (decimal?)x.Money)
            .SumAsync(cancellationToken) ?? 0m;
        var pendingCount = await dbContext.Orders
            .AsNoTracking()
            .CountAsync(x => x.PayStatus == PendingPayStatus, cancellationToken);
        var paidRows = await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.PayStatus == PaidPayStatus)
            .GroupBy(_ => 1)
            .Select(x => new
            {
                Count = x.Count(),
                Amount = x.Sum(item => (decimal?)item.Money) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DashboardOrdersSummary(
            TodayCount: todayCount,
            TodayPaidCount: todayPaidCount,
            TodayIncome: todayIncome,
            PendingCount: pendingCount,
            PaidCount: paidRows?.Count ?? 0,
            PaidAmount: paidRows?.Amount ?? 0m);
    }

    private async Task<DashboardWalletsSummary> GetWalletsSummaryAsync(
        DateTime today,
        DateTime tomorrow,
        CancellationToken cancellationToken)
    {
        var totalBalance = await dbContext.AccountWallets
            .AsNoTracking()
            .Select(x => (decimal?)x.Money)
            .SumAsync(cancellationToken) ?? 0m;
        var todayRechargeRows = await dbContext.Transactions
            .AsNoTracking()
            .Where(x =>
                x.Type == AccountOperations.RechargeTransactionType &&
                x.PayStatus == PaidPayStatus &&
                x.PayTime >= today &&
                x.PayTime < tomorrow)
            .GroupBy(_ => 1)
            .Select(x => new
            {
                Count = x.Count(),
                Amount = x.Sum(item => (decimal?)item.Money) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DashboardWalletsSummary(
            TotalBalance: totalBalance,
            TodayRechargeCount: todayRechargeRows?.Count ?? 0,
            TodayRechargeAmount: todayRechargeRows?.Amount ?? 0m);
    }

    private async Task<DashboardSubscriptionsSummary> GetSubscriptionsSummaryAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var expiringBefore = nowUtc.AddDays(7);
        var statusRows = await dbContext.Subscriptions
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() })
            .ToListAsync(cancellationToken);
        var validActiveCount = await dbContext.Subscriptions
            .AsNoTracking()
            .CountAsync(x => x.Status == SubscriptionStatus.Active && x.ExpiresAtUtc > nowUtc, cancellationToken);
        var expiringSoonCount = await dbContext.Subscriptions
            .AsNoTracking()
            .CountAsync(x =>
                x.Status == SubscriptionStatus.Active &&
                x.ExpiresAtUtc > nowUtc &&
                x.ExpiresAtUtc <= expiringBefore,
                cancellationToken);

        return new DashboardSubscriptionsSummary(
            TotalCount: statusRows.Sum(x => x.Count),
            ActiveCount: statusRows.Where(x => x.Status == SubscriptionStatus.Active).Sum(x => x.Count),
            ValidActiveCount: validActiveCount,
            ExpiredCount: statusRows.Where(x => x.Status == SubscriptionStatus.Expired).Sum(x => x.Count),
            CanceledCount: statusRows.Where(x => x.Status == SubscriptionStatus.Canceled).Sum(x => x.Count),
            ExpiringSoonCount: expiringSoonCount);
    }

    private async Task<DashboardOperationsSummary> GetOperationsSummaryAsync(CancellationToken cancellationToken)
    {
        var pendingReviewCount = await dbContext.AssetReviews
            .AsNoTracking()
            .CountAsync(x => x.Status == AssetReviewStatus.Pending, cancellationToken);
        var pendingCreatorCount = await dbContext.CreatorProfiles
            .AsNoTracking()
            .CountAsync(x => x.Status == CreatorStatus.Pending, cancellationToken);
        var pendingSettlementRows = await dbContext.CreatorSettlements
            .AsNoTracking()
            .Where(x => x.Status == SettlementStatus.Pending)
            .GroupBy(_ => 1)
            .Select(x => new
            {
                Count = x.Count(),
                Amount = x.Sum(item => (decimal?)item.NetAmount) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DashboardOperationsSummary(
            PendingReviewCount: pendingReviewCount,
            PendingCreatorCount: pendingCreatorCount,
            PendingSettlementCount: pendingSettlementRows?.Count ?? 0,
            PendingSettlementAmount: pendingSettlementRows?.Amount ?? 0m);
    }

    private async Task<IReadOnlyList<DashboardTrendItem>> GetTrendAsync(
        DateTime trendStart,
        DateTime tomorrow,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.PayStatus == PaidPayStatus && x.PayTime >= trendStart && x.PayTime < tomorrow)
            .Select(x => new { x.PayTime, x.Money })
            .ToListAsync(cancellationToken);
        var groups = rows
            .Where(x => x.PayTime.HasValue)
            .GroupBy(x => x.PayTime!.Value.Date)
            .ToDictionary(
                x => x.Key,
                x => new
                {
                    OrderCount = x.Count(),
                    IncomeAmount = x.Sum(item => item.Money)
                });

        return Enumerable.Range(0, 7)
            .Select(offset => trendStart.AddDays(offset))
            .Select(day =>
            {
                var exists = groups.TryGetValue(day.Date, out var group);
                return new DashboardTrendItem(
                    Date: day.ToString("yyyy-MM-dd"),
                    OrderCount: exists ? group!.OrderCount : 0,
                    IncomeAmount: exists ? group!.IncomeAmount : 0m);
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<DashboardHotAssetItem>> GetHotAssetsAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Assets
            .AsNoTracking()
            .OrderByDescending(x => x.DownloadCount)
            .ThenBy(x => x.Name)
            .Take(6)
            .Select(x => new { x.ID, x.Name, x.Slug, x.DownloadCount, x.Status })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new DashboardHotAssetItem(
                x.ID,
                x.Name,
                x.Slug,
                x.DownloadCount,
                x.Status,
                GetAssetStatusName(x.Status)))
            .ToArray();
    }

    private static DashboardRuntimeSummary GetRuntimeSummary()
    {
        using var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;

        return new DashboardRuntimeSummary(
            Framework: RuntimeInformation.FrameworkDescription,
            Os: RuntimeInformation.OSDescription,
            ProcessName: process.ProcessName,
            ProcessId: Environment.ProcessId,
            UptimeSeconds: (long)Math.Max(0, uptime.TotalSeconds),
            WorkingSetMb: Math.Round(process.WorkingSet64 / 1024m / 1024m, 1),
            ManagedHeapMb: Math.Round(GC.GetTotalMemory(false) / 1024m / 1024m, 1),
            ThreadCount: process.Threads.Count);
    }

    private DashboardMcpSummary GetMcpSummary()
        => new(
            Enabled: configuration.GetValue("Mcp:Enabled", false),
            PermitPerMinute: configuration.GetValue("Mcp:RateLimit:PermitPerMinute", 60),
            QueueLimit: configuration.GetValue("Mcp:RateLimit:QueueLimit", 0));

    private static string GetAssetStatusName(AssetStatus status) => status switch
    {
        AssetStatus.Draft => "草稿",
        AssetStatus.Published => "已发布",
        AssetStatus.Hidden => "隐藏",
        AssetStatus.Offline => "下架",
        _ => status.ToString()
    };
}

public sealed record DashboardSummaryResult(
    DateTimeOffset GeneratedAtUtc,
    DashboardAccountsSummary Accounts,
    DashboardAssetsSummary Assets,
    DashboardOrdersSummary Orders,
    DashboardWalletsSummary Wallets,
    DashboardSubscriptionsSummary Subscriptions,
    DashboardOperationsSummary Operations,
    IReadOnlyList<DashboardTrendItem> Trend,
    IReadOnlyList<DashboardHotAssetItem> HotAssets,
    DashboardRuntimeSummary Runtime,
    DashboardMcpSummary Mcp);

public sealed record DashboardAccountsSummary(
    int TotalCount,
    int EnabledCount,
    int DisabledCount,
    int FrozenCount);

public sealed record DashboardAssetsSummary(
    int TotalCount,
    int PublishedCount,
    int DraftCount,
    int HiddenCount,
    int OfflineCount,
    int FeaturedCount,
    int PendingReviewCount);

public sealed record DashboardOrdersSummary(
    int TodayCount,
    int TodayPaidCount,
    decimal TodayIncome,
    int PendingCount,
    int PaidCount,
    decimal PaidAmount);

public sealed record DashboardWalletsSummary(
    decimal TotalBalance,
    int TodayRechargeCount,
    decimal TodayRechargeAmount);

public sealed record DashboardSubscriptionsSummary(
    int TotalCount,
    int ActiveCount,
    int ValidActiveCount,
    int ExpiredCount,
    int CanceledCount,
    int ExpiringSoonCount);

public sealed record DashboardOperationsSummary(
    int PendingReviewCount,
    int PendingCreatorCount,
    int PendingSettlementCount,
    decimal PendingSettlementAmount);

public sealed record DashboardTrendItem(
    string Date,
    int OrderCount,
    decimal IncomeAmount);

public sealed record DashboardHotAssetItem(
    string Id,
    string Name,
    string Slug,
    int DownloadCount,
    AssetStatus Status,
    string StatusName);

public sealed record DashboardRuntimeSummary(
    string Framework,
    string Os,
    string ProcessName,
    int ProcessId,
    long UptimeSeconds,
    decimal WorkingSetMb,
    decimal ManagedHeapMb,
    int ThreadCount);

public sealed record DashboardMcpSummary(
    bool Enabled,
    int PermitPerMinute,
    int QueueLimit);
