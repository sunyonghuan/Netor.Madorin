using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Orders;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Orders;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class OrdersController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(string? keyword, byte? status, int page = 1, CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.No.Contains(normalizedKeyword) ||
                x.Title.Contains(normalizedKeyword) ||
                x.Content.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var assetNames = await dbContext.Assets
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);
        var pricingPlanNames = await dbContext.PricingPlans
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);

        var orders = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = orders.Select(x => new OrderListItem(
                x.ID,
                x.No,
                x.Title,
                x.Account == null ? "未知用户" : x.Account.LoginUserName,
                GetLookupName(assetNames, x.AssetId, "未知资源"),
                GetLookupName(pricingPlanNames, x.PricingPlanId, "未知方案"),
                x.Money,
                x.Numbers,
                x.PayStatus,
                x.PayMethod,
                x.Status,
                x.PayTime,
                x.TimeStamp,
                dbContext.Transactions.Count(t => t.OrderId == x.ID || t.OrderNo == x.No)))
            .ToList();

        var paidStatus = (byte)2;
        var pendingStatus = (byte)1;
        var model = new OrderIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            PaidCount = await dbContext.Orders.CountAsync(x => x.PayStatus == paidStatus, cancellationToken),
            PendingCount = await dbContext.Orders.CountAsync(x => x.PayStatus == pendingStatus, cancellationToken),
            PaidAmount = await dbContext.Orders.Where(x => x.PayStatus == paidStatus).SumAsync(x => x.Money, cancellationToken),
            TransactionCount = await dbContext.Transactions.CountAsync(cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        byte? status,
        byte? payStatus,
        byte? payMethod,
        string? assetKeyword,
        string? pricingPlanKeyword,
        decimal? moneyMin,
        decimal? moneyMax,
        int? numbersMin,
        int? numbersMax,
        bool? hasTransaction,
        DateTime? payTimeStart,
        DateTime? payTimeEnd,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.No.Contains(normalizedKeyword) ||
                x.Title.Contains(normalizedKeyword) ||
                x.Content.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (payStatus is not null)
        {
            query = query.Where(x => x.PayStatus == payStatus);
        }

        if (payMethod is not null)
        {
            query = query.Where(x => x.PayMethod == payMethod);
        }

        if (!string.IsNullOrWhiteSpace(assetKeyword))
        {
            var normalizedAsset = assetKeyword.Trim();
            query = query.Where(x => dbContext.Assets.Any(a =>
                a.ID == x.AssetId &&
                (a.Name.Contains(normalizedAsset) || a.Slug.Contains(normalizedAsset) || a.DeveloperName.Contains(normalizedAsset))));
        }

        if (!string.IsNullOrWhiteSpace(pricingPlanKeyword))
        {
            var normalizedPlan = pricingPlanKeyword.Trim();
            query = query.Where(x => dbContext.PricingPlans.Any(p =>
                p.ID == x.PricingPlanId &&
                (p.Name.Contains(normalizedPlan) || p.Currency.Contains(normalizedPlan))));
        }

        if (moneyMin is not null)
        {
            query = query.Where(x => x.Money >= moneyMin);
        }

        if (moneyMax is not null)
        {
            query = query.Where(x => x.Money <= moneyMax);
        }

        if (numbersMin is not null)
        {
            query = query.Where(x => x.Numbers >= numbersMin);
        }

        if (numbersMax is not null)
        {
            query = query.Where(x => x.Numbers <= numbersMax);
        }

        if (payTimeStart is not null)
        {
            query = query.Where(x => x.PayTime >= payTimeStart);
        }

        if (payTimeEnd is not null)
        {
            var normalizedPayTimeEnd = NormalizeEndDate(payTimeEnd.Value);
            query = query.Where(x => x.PayTime < normalizedPayTimeEnd);
        }

        if (hasTransaction is true)
        {
            query = query.Where(x => dbContext.Transactions.Any(t => t.OrderId == x.ID || t.OrderNo == x.No));
        }
        else if (hasTransaction is false)
        {
            query = query.Where(x => !dbContext.Transactions.Any(t => t.OrderId == x.ID || t.OrderNo == x.No));
        }

        var count = await query.CountAsync(cancellationToken);
        var orders = await ApplyOrderSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var assetIds = orders
            .Select(x => x.AssetId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Cast<string>()
            .ToArray();
        var pricingPlanIds = orders
            .Select(x => x.PricingPlanId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Cast<string>()
            .ToArray();
        var orderIds = orders.Select(x => x.ID).ToArray();
        var orderNos = orders.Select(x => x.No).ToArray();

        var assetNames = await dbContext.Assets
            .AsNoTracking()
            .Where(x => assetIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);
        var pricingPlanNames = await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => pricingPlanIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);
        var transactionKeys = await dbContext.Transactions
            .AsNoTracking()
            .Where(x => (x.OrderId != null && orderIds.Contains(x.OrderId)) || orderNos.Contains(x.OrderNo))
            .Select(x => new { x.OrderId, x.OrderNo })
            .ToListAsync(cancellationToken);

        var rows = orders.Select(x =>
            {
                var transactionCount = transactionKeys.Count(t => t.OrderId == x.ID || t.OrderNo == x.No);
                return new OrderTableItem(
                    x.ID,
                    x.No,
                    x.Title,
                    x.Account?.LoginUserName ?? "未知用户",
                    GetLookupName(assetNames, x.AssetId, "未知资源"),
                    GetLookupName(pricingPlanNames, x.PricingPlanId, "未知方案"),
                    GetContentSummary(x.Content),
                    x.Money,
                    x.Numbers,
                    x.PayStatus,
                    GetPayStatusName(x.PayStatus),
                    GetPayStatusBadgeClass(x.PayStatus),
                    x.PayMethod,
                    GetPayMethodName(x.PayMethod),
                    x.Status,
                    GetOrderStatusName(x.Status),
                    GetOrderStatusBadgeClass(x.Status),
                    FormatDate(x.PayTime),
                    x.TimeStamp,
                    transactionCount);
            })
            .ToList();

        return Json(new LayuiTableResult<OrderTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        var assetName = await dbContext.Assets
            .AsNoTracking()
            .Where(x => x.ID == order.AssetId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "未知资源";
        var pricingPlanName = await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => x.ID == order.PricingPlanId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "未知方案";

        var transactions = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Where(x => x.OrderId == order.ID || x.OrderNo == order.No)
            .OrderByDescending(x => x.ID)
            .Select(x => new TransactionListItem(
                x.ID,
                x.No,
                x.OrderNo,
                x.ThirdNo,
                x.Account == null ? "未知用户" : x.Account.LoginUserName,
                x.Title,
                x.Money,
                x.RealMoney,
                x.Type,
                x.PayStatus,
                x.PayMethod,
                x.PayTime,
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        var model = new OrderDetailViewModel
        {
            Id = order.ID,
            No = order.No,
            Title = order.Title,
            AccountName = order.Account == null ? "未知用户" : order.Account.LoginUserName,
            AssetName = assetName,
            PricingPlanName = pricingPlanName,
            Content = order.Content,
            Money = order.Money,
            Numbers = order.Numbers,
            PayStatus = order.PayStatus,
            PayMethod = order.PayMethod,
            Status = order.Status,
            PayTime = order.PayTime,
            TimeStamp = order.TimeStamp,
            Transactions = transactions
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BatchOrders(string[] ids, byte status, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            TempData["AdminToast"] = "请先选择要操作的订单。";
            return RedirectToAction(nameof(Index));
        }

        var orders = await dbContext.Orders
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        foreach (var order in orders)
        {
            order.Status = status;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = $"已批量更新 {orders.Count} 条订单。";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Transactions(string? keyword, byte? payStatus, int page = 1, CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.No.Contains(normalizedKeyword) ||
                x.OrderNo.Contains(normalizedKeyword) ||
                x.ThirdNo.Contains(normalizedKeyword) ||
                x.Title.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (payStatus is not null)
        {
            query = query.Where(x => x.PayStatus == payStatus);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new TransactionListItem(
                x.ID,
                x.No,
                x.OrderNo,
                x.ThirdNo,
                x.Account == null ? "未知用户" : x.Account.LoginUserName,
                x.Title,
                x.Money,
                x.RealMoney,
                x.Type,
                x.PayStatus,
                x.PayMethod,
                x.PayTime,
                x.TimeStamp))
            .ToListAsync(cancellationToken);

        var model = new TransactionIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            PayStatus = payStatus,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount
        };

        return View(model);
    }

    public async Task<IActionResult> TransactionsTableData(
        string? keyword,
        string? orderId,
        string? orderNo,
        string? accountKeyword,
        string? thirdNo,
        bool? hasThirdNo,
        byte? type,
        byte? payStatus,
        byte? payMethod,
        decimal? moneyMin,
        decimal? moneyMax,
        decimal? realMoneyMin,
        decimal? realMoneyMax,
        DateTime? payTimeStart,
        DateTime? payTimeEnd,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            var normalizedOrderId = orderId.Trim();
            query = query.Where(x => x.OrderId == normalizedOrderId);
        }

        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var normalizedOrderNo = orderNo.Trim();
            query = query.Where(x => x.OrderNo == normalizedOrderNo);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.No.Contains(normalizedKeyword) ||
                x.OrderNo.Contains(normalizedKeyword) ||
                x.ThirdNo.Contains(normalizedKeyword) ||
                x.Title.Contains(normalizedKeyword) ||
                (x.Account != null && x.Account.LoginUserName.Contains(normalizedKeyword)));
        }

        if (!string.IsNullOrWhiteSpace(accountKeyword))
        {
            var normalizedAccount = accountKeyword.Trim();
            query = query.Where(x => x.Account != null && x.Account.LoginUserName.Contains(normalizedAccount));
        }

        if (!string.IsNullOrWhiteSpace(thirdNo))
        {
            var normalizedThirdNo = thirdNo.Trim();
            query = query.Where(x => x.ThirdNo.Contains(normalizedThirdNo));
        }

        if (hasThirdNo is true)
        {
            query = query.Where(x => x.ThirdNo != string.Empty);
        }
        else if (hasThirdNo is false)
        {
            query = query.Where(x => x.ThirdNo == string.Empty);
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
        }

        if (payStatus is not null)
        {
            query = query.Where(x => x.PayStatus == payStatus);
        }

        if (payMethod is not null)
        {
            query = query.Where(x => x.PayMethod == payMethod);
        }

        if (moneyMin is not null)
        {
            query = query.Where(x => x.Money >= moneyMin);
        }

        if (moneyMax is not null)
        {
            query = query.Where(x => x.Money <= moneyMax);
        }

        if (realMoneyMin is not null)
        {
            query = query.Where(x => x.RealMoney >= realMoneyMin);
        }

        if (realMoneyMax is not null)
        {
            query = query.Where(x => x.RealMoney <= realMoneyMax);
        }

        if (payTimeStart is not null)
        {
            query = query.Where(x => x.PayTime >= payTimeStart);
        }

        if (payTimeEnd is not null)
        {
            var normalizedPayTimeEnd = NormalizeEndDate(payTimeEnd.Value);
            query = query.Where(x => x.PayTime < normalizedPayTimeEnd);
        }

        var count = await query.CountAsync(cancellationToken);
        var transactions = await ApplyTransactionSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = transactions
            .Select(x => new TransactionTableItem(
                x.ID,
                x.No,
                x.OrderNo,
                x.ThirdNo,
                x.Account?.LoginUserName ?? "未知用户",
                x.Title,
                x.Money,
                x.RealMoney,
                x.Type,
                GetTransactionTypeName(x.Type),
                x.PayStatus,
                GetPayStatusName(x.PayStatus),
                GetPayStatusBadgeClass(x.PayStatus),
                x.PayMethod,
                GetPayMethodName(x.PayMethod),
                FormatDate(x.PayTime),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<TransactionTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BatchTransactions(string[] ids, byte payStatus, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            TempData["AdminToast"] = "请先选择要操作的交易流水。";
            return RedirectToAction(nameof(Transactions));
        }

        var transactions = await dbContext.Transactions
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        foreach (var transaction in transactions)
        {
            transaction.PayStatus = payStatus;
            if (payStatus == 2)
            {
                transaction.PayTime ??= DateTime.Now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = $"已批量更新 {transactions.Count} 条交易流水。";
        return RedirectToAction(nameof(Transactions));
    }

    private static string GetLookupName(IReadOnlyDictionary<string, string> values, string? id, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(id) && values.TryGetValue(id, out var name))
        {
            return name;
        }

        return fallback;
    }

    private static IQueryable<Order> ApplyOrderSorting(IQueryable<Order> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "no" => isAscending ? query.OrderBy(x => x.No) : query.OrderByDescending(x => x.No),
            "title" => isAscending ? query.OrderBy(x => x.Title) : query.OrderByDescending(x => x.Title),
            "money" => isAscending ? query.OrderBy(x => x.Money) : query.OrderByDescending(x => x.Money),
            "numbers" => isAscending ? query.OrderBy(x => x.Numbers) : query.OrderByDescending(x => x.Numbers),
            "payStatus" => isAscending ? query.OrderBy(x => x.PayStatus) : query.OrderByDescending(x => x.PayStatus),
            "payMethod" => isAscending ? query.OrderBy(x => x.PayMethod) : query.OrderByDescending(x => x.PayMethod),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "payTimeText" => isAscending ? query.OrderBy(x => x.PayTime) : query.OrderByDescending(x => x.PayTime),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.ID)
        };
    }

    private static IQueryable<Transaction> ApplyTransactionSorting(IQueryable<Transaction> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "no" => isAscending ? query.OrderBy(x => x.No) : query.OrderByDescending(x => x.No),
            "orderNo" => isAscending ? query.OrderBy(x => x.OrderNo) : query.OrderByDescending(x => x.OrderNo),
            "thirdNo" => isAscending ? query.OrderBy(x => x.ThirdNo) : query.OrderByDescending(x => x.ThirdNo),
            "title" => isAscending ? query.OrderBy(x => x.Title) : query.OrderByDescending(x => x.Title),
            "money" => isAscending ? query.OrderBy(x => x.Money) : query.OrderByDescending(x => x.Money),
            "realMoney" => isAscending ? query.OrderBy(x => x.RealMoney) : query.OrderByDescending(x => x.RealMoney),
            "type" => isAscending ? query.OrderBy(x => x.Type) : query.OrderByDescending(x => x.Type),
            "payStatus" => isAscending ? query.OrderBy(x => x.PayStatus) : query.OrderByDescending(x => x.PayStatus),
            "payMethod" => isAscending ? query.OrderBy(x => x.PayMethod) : query.OrderByDescending(x => x.PayMethod),
            "payTimeText" => isAscending ? query.OrderBy(x => x.PayTime) : query.OrderByDescending(x => x.PayTime),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.ID)
        };
    }

    private static DateTime NormalizeEndDate(DateTime value)
    {
        return value.TimeOfDay == TimeSpan.Zero ? value.Date.AddDays(1) : value;
    }

    private static string GetContentSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= 40 ? normalizedValue : $"{normalizedValue[..40]}...";
    }

    private static string FormatDate(DateTime? value)
    {
        return value?.ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private static string GetPayStatusName(byte status) => status switch
    {
        0 => "未知",
        1 => "待支付",
        2 => "已支付",
        3 => "已取消",
        4 => "失败",
        _ => $"状态 {status}"
    };

    private static string GetPayStatusBadgeClass(byte status) => status switch
    {
        2 => "admin-badge-success",
        3 => "admin-badge-muted",
        4 => "admin-badge-danger",
        _ => "admin-badge-warning"
    };

    private static string GetPayMethodName(byte method) => method switch
    {
        0 => "未知",
        1 => "余额",
        2 => "微信",
        3 => "支付宝",
        4 => "银行卡",
        9 => "模拟支付",
        _ => $"方式 {method}"
    };

    private static string GetOrderStatusName(byte status) => status switch
    {
        0 => "未知",
        1 => "待处理",
        2 => "已完成",
        3 => "已取消",
        4 => "失败",
        _ => $"状态 {status}"
    };

    private static string GetOrderStatusBadgeClass(byte status) => status switch
    {
        2 => "admin-badge-success",
        3 => "admin-badge-muted",
        4 => "admin-badge-danger",
        _ => "admin-badge-warning"
    };

    private static string GetTransactionTypeName(byte type) => type switch
    {
        1 => "收入",
        2 => "支出",
        3 => "退款",
        AccountOperations.RechargeTransactionType => "钱包充值",
        _ => type == 0 ? "未知" : $"类型 {type}"
    };
}
