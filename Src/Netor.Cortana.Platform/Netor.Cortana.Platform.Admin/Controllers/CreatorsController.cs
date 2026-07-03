using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Creators;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Creators;
using Netor.Cortana.Platform.Entitys.Tables.Settlements;
using Netor.Cortana.Platform.Services.Creators;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class CreatorsController(PlatformDbContext dbContext, CreatorService creatorService) : Controller
{
    public async Task<IActionResult> Index(
        string? keyword,
        CreatorStatus? status,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.CreatorProfiles
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.DisplayName.Contains(normalizedKeyword) ||
                x.Bio.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var creators = await query
            .OrderByDescending(x => x.TimeStamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var accountIds = creators.Select(x => x.AccountId).ToList();

        var assetCounts = await dbContext.Assets
            .AsNoTracking()
            .Where(x => x.OwnerAccountId != null && accountIds.Contains(x.OwnerAccountId))
            .GroupBy(x => x.OwnerAccountId!)
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var pendingAmounts = await dbContext.CreatorSettlements
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.CreatorAccountId) && x.Status == SettlementStatus.Pending)
            .GroupBy(x => x.CreatorAccountId)
            .Select(x => new { AccountId = x.Key, Amount = x.Sum(s => s.NetAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Amount, cancellationToken);

        var model = new CreatorIndexViewModel
        {
            Items = creators.Select(x => new CreatorListItem(
                x.ID,
                x.AccountId,
                x.Account == null ? "未知账号" : x.Account.LoginUserName,
                x.DisplayName,
                x.Bio,
                x.Status,
                x.ApprovedAtUtc,
                assetCounts.GetValueOrDefault(x.AccountId),
                pendingAmounts.GetValueOrDefault(x.AccountId))).ToList(),
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            PendingCount = await dbContext.CreatorProfiles.CountAsync(x => x.Status == CreatorStatus.Pending, cancellationToken),
            ApprovedCount = await dbContext.CreatorProfiles.CountAsync(x => x.Status == CreatorStatus.Approved, cancellationToken),
            SuspendedCount = await dbContext.CreatorProfiles.CountAsync(x => x.Status == CreatorStatus.Suspended, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        string? displayName,
        string? accountKeyword,
        CreatorStatus? status,
        DateTimeOffset? approvedStart,
        DateTimeOffset? approvedEnd,
        int? assetMin,
        int? assetMax,
        decimal? pendingSettlementMin,
        decimal? pendingSettlementMax,
        decimal? settledSettlementMin,
        decimal? settledSettlementMax,
        bool? hasPendingReview,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.CreatorProfiles
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.DisplayName.Contains(normalizedKeyword) ||
                x.Bio.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var normalizedDisplayName = displayName.Trim();
            query = query.Where(x => x.DisplayName.Contains(normalizedDisplayName));
        }

        if (!string.IsNullOrWhiteSpace(accountKeyword))
        {
            var normalizedAccount = accountKeyword.Trim();
            query = query.Where(x => x.Account != null && x.Account.LoginUserName.Contains(normalizedAccount));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (approvedStart is not null)
        {
            query = query.Where(x => x.ApprovedAtUtc >= approvedStart);
        }

        if (approvedEnd is not null)
        {
            query = query.Where(x => x.ApprovedAtUtc < NormalizeEndDate(approvedEnd.Value));
        }

        if (assetMin is not null)
        {
            query = query.Where(x => dbContext.Assets.Count(a => a.OwnerAccountId == x.AccountId) >= assetMin);
        }

        if (assetMax is not null)
        {
            query = query.Where(x => dbContext.Assets.Count(a => a.OwnerAccountId == x.AccountId) <= assetMax);
        }

        if (pendingSettlementMin is not null)
        {
            query = query.Where(x => (dbContext.CreatorSettlements
                .Where(s => s.CreatorAccountId == x.AccountId && s.Status == SettlementStatus.Pending)
                .Sum(s => (decimal?)s.NetAmount) ?? 0m) >= pendingSettlementMin);
        }

        if (pendingSettlementMax is not null)
        {
            query = query.Where(x => (dbContext.CreatorSettlements
                .Where(s => s.CreatorAccountId == x.AccountId && s.Status == SettlementStatus.Pending)
                .Sum(s => (decimal?)s.NetAmount) ?? 0m) <= pendingSettlementMax);
        }

        if (settledSettlementMin is not null)
        {
            query = query.Where(x => (dbContext.CreatorSettlements
                .Where(s => s.CreatorAccountId == x.AccountId && s.Status == SettlementStatus.Settled)
                .Sum(s => (decimal?)s.NetAmount) ?? 0m) >= settledSettlementMin);
        }

        if (settledSettlementMax is not null)
        {
            query = query.Where(x => (dbContext.CreatorSettlements
                .Where(s => s.CreatorAccountId == x.AccountId && s.Status == SettlementStatus.Settled)
                .Sum(s => (decimal?)s.NetAmount) ?? 0m) <= settledSettlementMax);
        }

        if (hasPendingReview is true)
        {
            query = query.Where(x => dbContext.AssetReviews.Any(r =>
                r.Status == AssetReviewStatus.Pending &&
                dbContext.Assets.Any(a => a.ID == r.AssetId && a.OwnerAccountId == x.AccountId)));
        }
        else if (hasPendingReview is false)
        {
            query = query.Where(x => !dbContext.AssetReviews.Any(r =>
                r.Status == AssetReviewStatus.Pending &&
                dbContext.Assets.Any(a => a.ID == r.AssetId && a.OwnerAccountId == x.AccountId)));
        }

        var count = await query.CountAsync(cancellationToken);
        var creators = await ApplyCreatorSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var accountIds = creators.Select(x => x.AccountId).ToArray();

        var assetCounts = await dbContext.Assets
            .AsNoTracking()
            .Where(x => x.OwnerAccountId != null && accountIds.Contains(x.OwnerAccountId))
            .GroupBy(x => x.OwnerAccountId!)
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var pendingReviewCounts = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => x.Status == AssetReviewStatus.Pending)
            .Join(dbContext.Assets.AsNoTracking(), x => x.AssetId, x => x.ID, (review, asset) => new { asset.OwnerAccountId })
            .Where(x => x.OwnerAccountId != null && accountIds.Contains(x.OwnerAccountId))
            .GroupBy(x => x.OwnerAccountId!)
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var pendingAmounts = await GetSettlementAmountsAsync(accountIds, SettlementStatus.Pending, cancellationToken);
        var settledAmounts = await GetSettlementAmountsAsync(accountIds, SettlementStatus.Settled, cancellationToken);

        var rows = creators.Select(x => new CreatorTableItem(
                x.ID,
                x.AccountId,
                x.Account?.LoginUserName ?? "未知账号",
                x.DisplayName,
                string.IsNullOrWhiteSpace(x.Bio) ? "-" : x.Bio,
                x.Status,
                GetCreatorStatusName(x.Status),
                GetCreatorStatusBadgeClass(x.Status),
                FormatDate(x.ApprovedAtUtc),
                assetCounts.GetValueOrDefault(x.AccountId),
                pendingReviewCounts.GetValueOrDefault(x.AccountId),
                pendingAmounts.GetValueOrDefault(x.AccountId),
                settledAmounts.GetValueOrDefault(x.AccountId),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<CreatorTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var creator = await dbContext.CreatorProfiles
            .AsNoTracking()
            .Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);

        if (creator is null)
        {
            return NotFound();
        }

        var accountIds = new[] { creator.AccountId };
        var assetCount = await dbContext.Assets.CountAsync(x => x.OwnerAccountId == creator.AccountId, cancellationToken);
        var pendingReviewCount = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => x.Status == AssetReviewStatus.Pending)
            .Join(dbContext.Assets.AsNoTracking(), x => x.AssetId, x => x.ID, (review, asset) => asset)
            .CountAsync(x => x.OwnerAccountId == creator.AccountId, cancellationToken);
        var pendingAmounts = await GetSettlementAmountsAsync(accountIds, SettlementStatus.Pending, cancellationToken);
        var settledAmounts = await GetSettlementAmountsAsync(accountIds, SettlementStatus.Settled, cancellationToken);

        return View(new CreatorDetailViewModel
        {
            Id = creator.ID,
            AccountId = creator.AccountId,
            AccountName = creator.Account?.LoginUserName ?? "未知账号",
            DisplayName = creator.DisplayName,
            Bio = string.IsNullOrWhiteSpace(creator.Bio) ? "-" : creator.Bio,
            Status = creator.Status,
            StatusName = GetCreatorStatusName(creator.Status),
            StatusBadgeClass = GetCreatorStatusBadgeClass(creator.Status),
            ApprovedAtUtc = creator.ApprovedAtUtc,
            AssetCount = assetCount,
            PendingReviewCount = pendingReviewCount,
            PendingSettlementAmount = pendingAmounts.GetValueOrDefault(creator.AccountId),
            SettledSettlementAmount = settledAmounts.GetValueOrDefault(creator.AccountId)
        });
    }

    public async Task<IActionResult> Assets(string id, CancellationToken cancellationToken)
    {
        var model = await GetCreatorRelatedViewModelAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    public async Task<IActionResult> Settlements(string id, CancellationToken cancellationToken)
    {
        var model = await GetCreatorRelatedViewModelAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    public async Task<IActionResult> AssetsTableData(
        string accountId,
        string? keyword,
        AssetType? type,
        AssetStatus? status,
        int? downloadMin,
        int? downloadMax,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .Where(x => x.OwnerAccountId == accountId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x => x.Name.Contains(normalizedKeyword) || x.Slug.Contains(normalizedKeyword) || x.ShortDescription.Contains(normalizedKeyword));
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (downloadMin is not null)
        {
            query = query.Where(x => x.DownloadCount >= downloadMin);
        }

        if (downloadMax is not null)
        {
            query = query.Where(x => x.DownloadCount <= downloadMax);
        }

        var count = await query.CountAsync(cancellationToken);
        var assets = await ApplyAssetSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = assets.Select(x => new CreatorAssetTableItem(
                x.ID,
                x.Name,
                x.Slug,
                GetAssetTypeName(x.Type),
                GetAssetStatusName(x.Status),
                x.Category?.Name ?? "未分类",
                x.DownloadCount,
                x.Versions.Count,
                x.PricingPlans.Count,
                x.PublishedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"))
            .ToList();

        return Json(new LayuiTableResult<CreatorAssetTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> SettlementsTableData(
        string accountId,
        string? keyword,
        SettlementStatus? status,
        decimal? netMin,
        decimal? netMax,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.CreatorSettlements
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.Order)
            .Where(x => x.CreatorAccountId == accountId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Notes.Contains(normalizedKeyword) ||
                x.Currency.Contains(normalizedKeyword) ||
                (x.Asset != null && x.Asset.Name.Contains(normalizedKeyword)) ||
                (x.Order != null && x.Order.No.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (netMin is not null)
        {
            query = query.Where(x => x.NetAmount >= netMin);
        }

        if (netMax is not null)
        {
            query = query.Where(x => x.NetAmount <= netMax);
        }

        var count = await query.CountAsync(cancellationToken);
        var settlements = await ApplySettlementSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = settlements.Select(x => new CreatorSettlementTableItem(
                x.ID,
                x.Asset?.Name ?? "未知资源",
                x.Order?.No ?? x.OrderId,
                x.GrossAmount,
                x.PlatformFeeAmount,
                x.NetAmount,
                x.Currency,
                GetSettlementStatusName(x.Status),
                GetSettlementStatusBadgeClass(x.Status),
                FormatDate(x.SettledAtUtc),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<CreatorSettlementTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(string id, CancellationToken cancellationToken)
    {
        var creator = await dbContext.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (creator is null)
        {
            return NotFound();
        }

        var approved = await creatorService.ApproveCreatorAsync(creator.AccountId, cancellationToken);
        TempData["AdminToast"] = approved ? "创作者已通过审核。" : "创作者不存在。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(string id, CancellationToken cancellationToken)
    {
        var creator = await dbContext.CreatorProfiles.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (creator is null)
        {
            return NotFound();
        }

        creator.Status = CreatorStatus.Suspended;
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = "创作者已暂停。";
        return RedirectToAction(nameof(Index));
    }

    private static IQueryable<CreatorProfile> ApplyCreatorSorting(IQueryable<CreatorProfile> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "displayName" => isAscending ? query.OrderBy(x => x.DisplayName) : query.OrderByDescending(x => x.DisplayName),
            "accountName" => isAscending ? query.OrderBy(x => x.Account!.LoginUserName) : query.OrderByDescending(x => x.Account!.LoginUserName),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "approvedAtText" => isAscending ? query.OrderBy(x => x.ApprovedAtUtc) : query.OrderByDescending(x => x.ApprovedAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.TimeStamp)
        };
    }

    private static IQueryable<Asset> ApplyAssetSorting(IQueryable<Asset> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "name" => isAscending ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
            "slug" => isAscending ? query.OrderBy(x => x.Slug) : query.OrderByDescending(x => x.Slug),
            "typeName" => isAscending ? query.OrderBy(x => x.Type) : query.OrderByDescending(x => x.Type),
            "statusName" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "downloadCount" => isAscending ? query.OrderBy(x => x.DownloadCount) : query.OrderByDescending(x => x.DownloadCount),
            "publishedAtText" => isAscending ? query.OrderBy(x => x.PublishedAtUtc) : query.OrderByDescending(x => x.PublishedAtUtc),
            _ => query.OrderByDescending(x => x.TimeStamp)
        };
    }

    private static IQueryable<CreatorSettlement> ApplySettlementSorting(IQueryable<CreatorSettlement> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "assetName" => isAscending ? query.OrderBy(x => x.Asset!.Name) : query.OrderByDescending(x => x.Asset!.Name),
            "orderNo" => isAscending ? query.OrderBy(x => x.Order!.No) : query.OrderByDescending(x => x.Order!.No),
            "grossAmount" => isAscending ? query.OrderBy(x => x.GrossAmount) : query.OrderByDescending(x => x.GrossAmount),
            "platformFeeAmount" => isAscending ? query.OrderBy(x => x.PlatformFeeAmount) : query.OrderByDescending(x => x.PlatformFeeAmount),
            "netAmount" => isAscending ? query.OrderBy(x => x.NetAmount) : query.OrderByDescending(x => x.NetAmount),
            "statusName" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "settledAtText" => isAscending ? query.OrderBy(x => x.SettledAtUtc) : query.OrderByDescending(x => x.SettledAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.TimeStamp)
        };
    }

    private async Task<CreatorRelatedViewModel?> GetCreatorRelatedViewModelAsync(string id, CancellationToken cancellationToken)
    {
        return await dbContext.CreatorProfiles
            .AsNoTracking()
            .Include(x => x.Account)
            .Where(x => x.ID == id)
            .Select(x => new CreatorRelatedViewModel
            {
                CreatorId = x.ID,
                AccountId = x.AccountId,
                DisplayName = x.DisplayName,
                AccountName = x.Account == null ? "未知账号" : x.Account.LoginUserName
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Dictionary<string, decimal>> GetSettlementAmountsAsync(string[] accountIds, SettlementStatus status, CancellationToken cancellationToken)
    {
        return await dbContext.CreatorSettlements
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.CreatorAccountId) && x.Status == status)
            .GroupBy(x => x.CreatorAccountId)
            .Select(x => new { AccountId = x.Key, Amount = x.Sum(s => s.NetAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Amount, cancellationToken);
    }

    private static DateTimeOffset NormalizeEndDate(DateTimeOffset value)
    {
        return value.TimeOfDay == TimeSpan.Zero ? new DateTimeOffset(value.Date.AddDays(1), value.Offset) : value;
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private static string GetCreatorStatusName(CreatorStatus status) => status switch
    {
        CreatorStatus.Pending => "待审核",
        CreatorStatus.Approved => "已通过",
        CreatorStatus.Suspended => "已暂停",
        _ => status.ToString()
    };

    private static string GetCreatorStatusBadgeClass(CreatorStatus status) => status switch
    {
        CreatorStatus.Approved => "admin-badge-success",
        CreatorStatus.Suspended => "admin-badge-danger",
        _ => "admin-badge-warning"
    };

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static string GetAssetStatusName(AssetStatus status) => status switch
    {
        AssetStatus.Draft => "草稿",
        AssetStatus.Published => "已发布",
        AssetStatus.Hidden => "隐藏",
        AssetStatus.Offline => "下架",
        _ => status.ToString()
    };

    private static string GetSettlementStatusName(SettlementStatus status) => status switch
    {
        SettlementStatus.Pending => "待结算",
        SettlementStatus.Settled => "已结算",
        SettlementStatus.Frozen => "已冻结",
        SettlementStatus.Rejected => "已驳回",
        _ => status.ToString()
    };

    private static string GetSettlementStatusBadgeClass(SettlementStatus status) => status switch
    {
        SettlementStatus.Settled => "admin-badge-success",
        SettlementStatus.Frozen => "admin-badge-warning",
        SettlementStatus.Rejected => "admin-badge-danger",
        _ => "admin-badge-muted"
    };
}
