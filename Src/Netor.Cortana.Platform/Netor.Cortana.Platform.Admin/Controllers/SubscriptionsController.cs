using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Subscriptions;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class SubscriptionsController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(
        string? keyword,
        SubscriptionStatus? status,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.Subscriptions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Asset)
            .Include(x => x.PricingPlan)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && x.Asset.Name.Contains(normalizedKeyword)) ||
                (x.PricingPlan != null && x.PricingPlan.Name.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SubscriptionListItem(
                x.ID,
                x.Account == null ? "未知用户" : x.Account.LoginUserName,
                x.Asset == null ? "未知资源" : x.Asset.Name,
                x.PricingPlan == null ? "未知方案" : x.PricingPlan.Name,
                GetSubscriptionStatusName(x.Status),
                x.Status,
                x.StartedAtUtc,
                x.ExpiresAtUtc,
                x.CanceledAtUtc,
                x.OrderId,
                dbContext.DownloadRecords.Count(d => d.SubscriptionId == x.ID)))
            .ToListAsync(cancellationToken);

        var model = new SubscriptionIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            ActiveCount = await dbContext.Subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Active, cancellationToken),
            ExpiredCount = await dbContext.Subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Expired, cancellationToken),
            CanceledCount = await dbContext.Subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Canceled, cancellationToken),
            DownloadCount = await dbContext.DownloadRecords.CountAsync(cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        SubscriptionStatus? status,
        string? accountKeyword,
        string? assetKeyword,
        string? pricingPlanKeyword,
        bool? hasOrder,
        int? downloadMin,
        int? downloadMax,
        DateTimeOffset? startedStart,
        DateTimeOffset? startedEnd,
        DateTimeOffset? expiresStart,
        DateTimeOffset? expiresEnd,
        DateTimeOffset? canceledStart,
        DateTimeOffset? canceledEnd,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Subscriptions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Asset)
            .Include(x => x.PricingPlan)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.ID.Contains(normalizedKeyword) ||
                (x.OrderId != null && x.OrderId.Contains(normalizedKeyword)) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && (x.Asset.Name.Contains(normalizedKeyword) || x.Asset.Slug.Contains(normalizedKeyword))) ||
                (x.PricingPlan != null && x.PricingPlan.Name.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(accountKeyword))
        {
            var normalizedAccount = accountKeyword.Trim();
            query = query.Where(x => x.Account != null && x.Account.LoginUserName.Contains(normalizedAccount));
        }

        if (!string.IsNullOrWhiteSpace(assetKeyword))
        {
            var normalizedAsset = assetKeyword.Trim();
            query = query.Where(x => x.Asset != null && (x.Asset.Name.Contains(normalizedAsset) || x.Asset.Slug.Contains(normalizedAsset)));
        }

        if (!string.IsNullOrWhiteSpace(pricingPlanKeyword))
        {
            var normalizedPlan = pricingPlanKeyword.Trim();
            query = query.Where(x => x.PricingPlan != null && (x.PricingPlan.Name.Contains(normalizedPlan) || x.PricingPlan.Currency.Contains(normalizedPlan)));
        }

        if (hasOrder is true)
        {
            query = query.Where(x => x.OrderId != null);
        }
        else if (hasOrder is false)
        {
            query = query.Where(x => x.OrderId == null);
        }

        if (downloadMin is not null)
        {
            query = query.Where(x => dbContext.DownloadRecords.Count(d => d.SubscriptionId == x.ID) >= downloadMin);
        }

        if (downloadMax is not null)
        {
            query = query.Where(x => dbContext.DownloadRecords.Count(d => d.SubscriptionId == x.ID) <= downloadMax);
        }

        if (startedStart is not null)
        {
            query = query.Where(x => x.StartedAtUtc >= startedStart);
        }

        if (startedEnd is not null)
        {
            query = query.Where(x => x.StartedAtUtc < NormalizeEndDate(startedEnd.Value));
        }

        if (expiresStart is not null)
        {
            query = query.Where(x => x.ExpiresAtUtc >= expiresStart);
        }

        if (expiresEnd is not null)
        {
            query = query.Where(x => x.ExpiresAtUtc < NormalizeEndDate(expiresEnd.Value));
        }

        if (canceledStart is not null)
        {
            query = query.Where(x => x.CanceledAtUtc >= canceledStart);
        }

        if (canceledEnd is not null)
        {
            query = query.Where(x => x.CanceledAtUtc < NormalizeEndDate(canceledEnd.Value));
        }

        var count = await query.CountAsync(cancellationToken);
        var subscriptions = await ApplySubscriptionSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var subscriptionIds = subscriptions.Select(x => x.ID).ToArray();
        var downloadCounts = await dbContext.DownloadRecords
            .AsNoTracking()
            .Where(x => x.SubscriptionId != null && subscriptionIds.Contains(x.SubscriptionId))
            .GroupBy(x => x.SubscriptionId!)
            .Select(x => new { SubscriptionId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.SubscriptionId, x => x.Count, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var rows = subscriptions.Select(x => new SubscriptionTableItem(
                x.ID,
                x.Account?.LoginUserName ?? "未知用户",
                x.Asset?.Name ?? "未知资源",
                x.PricingPlan?.Name ?? "未知方案",
                GetSubscriptionStatusName(x.Status),
                x.Status,
                GetSubscriptionStatusBadgeClass(x.Status),
                FormatDate(x.StartedAtUtc),
                FormatDate(x.ExpiresAtUtc),
                FormatDate(x.CanceledAtUtc),
                (int)Math.Ceiling((x.ExpiresAtUtc - now).TotalDays),
                string.IsNullOrWhiteSpace(x.OrderId) ? "-" : x.OrderId,
                downloadCounts.GetValueOrDefault(x.ID),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<SubscriptionTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Downloads(string? keyword, int page = 1, CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.DownloadRecords
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && x.Asset.Name.Contains(normalizedKeyword)) ||
                (x.AssetVersion != null && x.AssetVersion.VersionName.Contains(normalizedKeyword)) ||
                (x.IpAddress != null && x.IpAddress.Contains(normalizedKeyword)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new DownloadRecordListItem(
                x.Account == null ? "未知用户" : x.Account.LoginUserName,
                x.Asset == null ? "未知资源" : x.Asset.Name,
                x.AssetVersion == null ? "未知版本" : x.AssetVersion.VersionName,
                x.IpAddress,
                x.UserAgent,
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        var model = new DownloadRecordIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount
        };

        return View(model);
    }

    public async Task<IActionResult> DownloadsTableData(
        string? keyword,
        string? subscriptionId,
        string? accountKeyword,
        string? assetKeyword,
        string? versionKeyword,
        string? ipAddress,
        string? userAgent,
        bool? hasSubscription,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.DownloadRecords
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            var normalizedSubscriptionId = subscriptionId.Trim();
            query = query.Where(x => x.SubscriptionId == normalizedSubscriptionId);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.ID.Contains(normalizedKeyword) ||
                (x.SubscriptionId != null && x.SubscriptionId.Contains(normalizedKeyword)) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && (x.Asset.Name.Contains(normalizedKeyword) || x.Asset.Slug.Contains(normalizedKeyword))) ||
                (x.AssetVersion != null && x.AssetVersion.VersionName.Contains(normalizedKeyword)) ||
                (x.IpAddress != null && x.IpAddress.Contains(normalizedKeyword)) ||
                (x.UserAgent != null && x.UserAgent.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(accountKeyword))
        {
            var normalizedAccount = accountKeyword.Trim();
            query = query.Where(x => x.Account != null && x.Account.LoginUserName.Contains(normalizedAccount));
        }

        if (!string.IsNullOrWhiteSpace(assetKeyword))
        {
            var normalizedAsset = assetKeyword.Trim();
            query = query.Where(x => x.Asset != null && (x.Asset.Name.Contains(normalizedAsset) || x.Asset.Slug.Contains(normalizedAsset)));
        }

        if (!string.IsNullOrWhiteSpace(versionKeyword))
        {
            var normalizedVersion = versionKeyword.Trim();
            query = query.Where(x => x.AssetVersion != null && x.AssetVersion.VersionName.Contains(normalizedVersion));
        }

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var normalizedIp = ipAddress.Trim();
            query = query.Where(x => x.IpAddress != null && x.IpAddress.Contains(normalizedIp));
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var normalizedUserAgent = userAgent.Trim();
            query = query.Where(x => x.UserAgent != null && x.UserAgent.Contains(normalizedUserAgent));
        }

        if (hasSubscription is true)
        {
            query = query.Where(x => x.SubscriptionId != null);
        }
        else if (hasSubscription is false)
        {
            query = query.Where(x => x.SubscriptionId == null);
        }

        var count = await query.CountAsync(cancellationToken);
        var downloads = await ApplyDownloadSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = downloads.Select(x => new DownloadTableItem(
                x.ID,
                x.Account?.LoginUserName ?? "未知用户",
                x.Asset?.Name ?? "未知资源",
                x.AssetVersion?.VersionName ?? "未知版本",
                string.IsNullOrWhiteSpace(x.SubscriptionId) ? "-" : x.SubscriptionId,
                string.IsNullOrWhiteSpace(x.IpAddress) ? "-" : x.IpAddress,
                string.IsNullOrWhiteSpace(x.UserAgent) ? "-" : x.UserAgent,
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<DownloadTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken)
    {
        var subscription = await dbContext.Subscriptions.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        subscription.Status = SubscriptionStatus.Canceled;
        subscription.CanceledAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Batch(string[] ids, string operation, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            TempData["AdminToast"] = "请先选择要操作的订阅。";
            return RedirectToAction(nameof(Index));
        }

        var subscriptions = await dbContext.Subscriptions
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        foreach (var subscription in subscriptions)
        {
            switch (operation)
            {
                case "active":
                    subscription.Status = subscription.ExpiresAtUtc <= DateTimeOffset.UtcNow ? SubscriptionStatus.Expired : SubscriptionStatus.Active;
                    subscription.CanceledAtUtc = null;
                    break;
                case "cancel":
                    subscription.Status = SubscriptionStatus.Canceled;
                    subscription.CanceledAtUtc ??= DateTimeOffset.UtcNow;
                    break;
                case "expired":
                    subscription.Status = SubscriptionStatus.Expired;
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = $"已批量处理 {subscriptions.Count} 条订阅。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(string id, CancellationToken cancellationToken)
    {
        var subscription = await dbContext.Subscriptions.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        subscription.Status = subscription.ExpiresAtUtc <= DateTimeOffset.UtcNow ? SubscriptionStatus.Expired : SubscriptionStatus.Active;
        subscription.CanceledAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    private static string GetSubscriptionStatusName(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "有效",
        SubscriptionStatus.Expired => "已过期",
        SubscriptionStatus.Canceled => "已取消",
        _ => status.ToString()
    };

    private static IQueryable<Subscription> ApplySubscriptionSorting(IQueryable<Subscription> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "accountName" => isAscending ? query.OrderBy(x => x.Account!.LoginUserName) : query.OrderByDescending(x => x.Account!.LoginUserName),
            "assetName" => isAscending ? query.OrderBy(x => x.Asset!.Name) : query.OrderByDescending(x => x.Asset!.Name),
            "pricingPlanName" => isAscending ? query.OrderBy(x => x.PricingPlan!.Name) : query.OrderByDescending(x => x.PricingPlan!.Name),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "startedAtText" => isAscending ? query.OrderBy(x => x.StartedAtUtc) : query.OrderByDescending(x => x.StartedAtUtc),
            "expiresAtText" => isAscending ? query.OrderBy(x => x.ExpiresAtUtc) : query.OrderByDescending(x => x.ExpiresAtUtc),
            "canceledAtText" => isAscending ? query.OrderBy(x => x.CanceledAtUtc) : query.OrderByDescending(x => x.CanceledAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.ID)
        };
    }

    private static IQueryable<DownloadRecord> ApplyDownloadSorting(IQueryable<DownloadRecord> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "accountName" => isAscending ? query.OrderBy(x => x.Account!.LoginUserName) : query.OrderByDescending(x => x.Account!.LoginUserName),
            "assetName" => isAscending ? query.OrderBy(x => x.Asset!.Name) : query.OrderByDescending(x => x.Asset!.Name),
            "versionName" => isAscending ? query.OrderBy(x => x.AssetVersion!.VersionName) : query.OrderByDescending(x => x.AssetVersion!.VersionName),
            "subscriptionId" => isAscending ? query.OrderBy(x => x.SubscriptionId) : query.OrderByDescending(x => x.SubscriptionId),
            "ipAddress" => isAscending ? query.OrderBy(x => x.IpAddress) : query.OrderByDescending(x => x.IpAddress),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.ID)
        };
    }

    private static DateTimeOffset NormalizeEndDate(DateTimeOffset value)
    {
        return value.TimeOfDay == TimeSpan.Zero ? value.Date.AddDays(1) : value;
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private static string GetSubscriptionStatusBadgeClass(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "admin-badge-success",
        SubscriptionStatus.Expired => "admin-badge-warning",
        SubscriptionStatus.Canceled => "admin-badge-danger",
        _ => "admin-badge-muted"
    };
}
