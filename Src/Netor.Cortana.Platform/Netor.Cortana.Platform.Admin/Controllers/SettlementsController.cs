using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Settlements;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Settlements;
using Netor.Cortana.Platform.Services.Settlements;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class SettlementsController(PlatformDbContext dbContext, CreatorSettlementService settlementService) : Controller
{
    public async Task<IActionResult> Index(
        string? keyword,
        SettlementStatus? status,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.CreatorSettlements
            .AsNoTracking()
            .Include(x => x.CreatorAccount)
            .Include(x => x.Asset)
            .Include(x => x.Order)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Notes.Contains(normalizedKeyword) ||
                (x.CreatorAccount != null && x.CreatorAccount.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && x.Asset.Name.Contains(normalizedKeyword)) ||
                (x.Order != null && x.Order.No.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(x => x.TimeStamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SettlementListItem(
                x.ID,
                x.CreatorAccount == null ? "未知创作者" : x.CreatorAccount.LoginUserName,
                x.Asset == null ? "未知资源" : x.Asset.Name,
                x.Order == null ? x.OrderId : x.Order.No,
                x.GrossAmount,
                x.PlatformFeeAmount,
                x.NetAmount,
                x.Currency,
                x.Status,
                x.Notes,
                x.SettledAtUtc,
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        var model = new SettlementIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            PendingCount = await dbContext.CreatorSettlements.CountAsync(x => x.Status == SettlementStatus.Pending, cancellationToken),
            SettledCount = await dbContext.CreatorSettlements.CountAsync(x => x.Status == SettlementStatus.Settled, cancellationToken),
            PendingNetAmount = await dbContext.CreatorSettlements.Where(x => x.Status == SettlementStatus.Pending).SumAsync(x => x.NetAmount, cancellationToken),
            SettledNetAmount = await dbContext.CreatorSettlements.Where(x => x.Status == SettlementStatus.Settled).SumAsync(x => x.NetAmount, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        string? creatorKeyword,
        string? assetKeyword,
        string? orderNo,
        SettlementStatus? status,
        string? currency,
        decimal? grossMin,
        decimal? grossMax,
        decimal? feeMin,
        decimal? feeMax,
        decimal? netMin,
        decimal? netMax,
        DateTimeOffset? settledStart,
        DateTimeOffset? settledEnd,
        bool? hasNotes,
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
            .Include(x => x.CreatorAccount)
            .Include(x => x.Asset)
            .Include(x => x.Order)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Notes.Contains(normalizedKeyword) ||
                x.Currency.Contains(normalizedKeyword) ||
                (x.CreatorAccount != null && x.CreatorAccount.LoginUserName.Contains(normalizedKeyword)) ||
                (x.Asset != null && x.Asset.Name.Contains(normalizedKeyword)) ||
                (x.Order != null && x.Order.No.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(creatorKeyword))
        {
            var normalizedCreator = creatorKeyword.Trim();
            query = query.Where(x => x.CreatorAccount != null && x.CreatorAccount.LoginUserName.Contains(normalizedCreator));
        }

        if (!string.IsNullOrWhiteSpace(assetKeyword))
        {
            var normalizedAsset = assetKeyword.Trim();
            query = query.Where(x => x.Asset != null && (x.Asset.Name.Contains(normalizedAsset) || x.Asset.Slug.Contains(normalizedAsset)));
        }

        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var normalizedOrderNo = orderNo.Trim();
            query = query.Where(x => x.Order != null && x.Order.No.Contains(normalizedOrderNo));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(currency))
        {
            var normalizedCurrency = currency.Trim().ToUpperInvariant();
            query = query.Where(x => x.Currency == normalizedCurrency);
        }

        if (grossMin is not null)
        {
            query = query.Where(x => x.GrossAmount >= grossMin);
        }

        if (grossMax is not null)
        {
            query = query.Where(x => x.GrossAmount <= grossMax);
        }

        if (feeMin is not null)
        {
            query = query.Where(x => x.PlatformFeeAmount >= feeMin);
        }

        if (feeMax is not null)
        {
            query = query.Where(x => x.PlatformFeeAmount <= feeMax);
        }

        if (netMin is not null)
        {
            query = query.Where(x => x.NetAmount >= netMin);
        }

        if (netMax is not null)
        {
            query = query.Where(x => x.NetAmount <= netMax);
        }

        if (settledStart is not null)
        {
            query = query.Where(x => x.SettledAtUtc >= settledStart);
        }

        if (settledEnd is not null)
        {
            query = query.Where(x => x.SettledAtUtc < NormalizeEndDate(settledEnd.Value));
        }

        if (hasNotes is true)
        {
            query = query.Where(x => x.Notes != string.Empty);
        }
        else if (hasNotes is false)
        {
            query = query.Where(x => x.Notes == string.Empty);
        }

        var count = await query.CountAsync(cancellationToken);
        var settlements = await ApplySettlementSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = settlements.Select(x => new SettlementTableItem(
                x.ID,
                x.CreatorAccount?.LoginUserName ?? "未知创作者",
                x.Asset?.Name ?? "未知资源",
                x.Order?.No ?? x.OrderId,
                x.GrossAmount,
                x.PlatformFeeAmount,
                x.NetAmount,
                x.Currency,
                x.Status,
                GetSettlementStatusName(x.Status),
                GetSettlementBadgeClass(x.Status),
                string.IsNullOrWhiteSpace(x.Notes) ? "-" : x.Notes,
                FormatDate(x.SettledAtUtc),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<SettlementTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var settlement = await dbContext.CreatorSettlements
            .AsNoTracking()
            .Include(x => x.CreatorAccount)
            .Include(x => x.Asset)
            .Include(x => x.Order)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);

        if (settlement is null)
        {
            return NotFound();
        }

        return View(new SettlementDetailViewModel
        {
            Id = settlement.ID,
            CreatorAccountName = settlement.CreatorAccount?.LoginUserName ?? "未知创作者",
            AssetName = settlement.Asset?.Name ?? "未知资源",
            OrderNo = settlement.Order?.No ?? settlement.OrderId,
            GrossAmount = settlement.GrossAmount,
            PlatformFeeAmount = settlement.PlatformFeeAmount,
            NetAmount = settlement.NetAmount,
            Currency = settlement.Currency,
            Status = settlement.Status,
            StatusName = GetSettlementStatusName(settlement.Status),
            StatusBadgeClass = GetSettlementBadgeClass(settlement.Status),
            Notes = string.IsNullOrWhiteSpace(settlement.Notes) ? "-" : settlement.Notes,
            SettledAtUtc = settlement.SettledAtUtc,
            TimeStamp = settlement.TimeStamp
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settle(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await settlementService.MarkSettledAsync(id, notes ?? "人工结算完成", cancellationToken);
        TempData["AdminToast"] = ok ? "结算记录已标记为已结算。" : "只有待结算记录可以标记结算。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Freeze(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await settlementService.FreezeAsync(id, notes ?? "人工冻结", cancellationToken);
        TempData["AdminToast"] = ok ? "结算记录已冻结。" : "已结算记录不能冻结。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(string id, string? notes, CancellationToken cancellationToken)
    {
        var ok = await settlementService.RejectAsync(id, notes ?? "人工驳回", cancellationToken);
        TempData["AdminToast"] = ok ? "结算记录已驳回。" : "已结算记录不能驳回。";
        return RedirectToAction(nameof(Index));
    }

    private static IQueryable<CreatorSettlement> ApplySettlementSorting(IQueryable<CreatorSettlement> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "creatorAccountName" => isAscending ? query.OrderBy(x => x.CreatorAccount!.LoginUserName) : query.OrderByDescending(x => x.CreatorAccount!.LoginUserName),
            "assetName" => isAscending ? query.OrderBy(x => x.Asset!.Name) : query.OrderByDescending(x => x.Asset!.Name),
            "orderNo" => isAscending ? query.OrderBy(x => x.Order!.No) : query.OrderByDescending(x => x.Order!.No),
            "grossAmount" => isAscending ? query.OrderBy(x => x.GrossAmount) : query.OrderByDescending(x => x.GrossAmount),
            "platformFeeAmount" => isAscending ? query.OrderBy(x => x.PlatformFeeAmount) : query.OrderByDescending(x => x.PlatformFeeAmount),
            "netAmount" => isAscending ? query.OrderBy(x => x.NetAmount) : query.OrderByDescending(x => x.NetAmount),
            "currency" => isAscending ? query.OrderBy(x => x.Currency) : query.OrderByDescending(x => x.Currency),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "settledAtText" => isAscending ? query.OrderBy(x => x.SettledAtUtc) : query.OrderByDescending(x => x.SettledAtUtc),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.TimeStamp)
        };
    }

    private static DateTimeOffset NormalizeEndDate(DateTimeOffset value)
    {
        return value.TimeOfDay == TimeSpan.Zero ? new DateTimeOffset(value.Date.AddDays(1), value.Offset) : value;
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private static string GetSettlementStatusName(SettlementStatus status) => status switch
    {
        SettlementStatus.Pending => "待结算",
        SettlementStatus.Settled => "已结算",
        SettlementStatus.Frozen => "已冻结",
        SettlementStatus.Rejected => "已驳回",
        _ => status.ToString()
    };

    private static string GetSettlementBadgeClass(SettlementStatus status) => status switch
    {
        SettlementStatus.Settled => "admin-badge-success",
        SettlementStatus.Frozen => "admin-badge-warning",
        SettlementStatus.Rejected => "admin-badge-danger",
        _ => "admin-badge-muted"
    };
}
