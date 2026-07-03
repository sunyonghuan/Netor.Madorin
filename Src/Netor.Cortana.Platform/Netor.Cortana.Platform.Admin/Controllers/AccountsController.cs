using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Mcp;
using Netor.Cortana.Platform.Admin.Models.Accounts;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using System.Security.Claims;
using System.Text.Json;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class AccountsController(
    PlatformDbContext dbContext,
    AccountOperations accountOperations,
    AdminAuditService auditService) : Controller
{
    public async Task<IActionResult> Index(string? keyword, byte? status, int page = 1, CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.Accounts
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.LoginUserName.Contains(normalizedKeyword) ||
                x.Phone.Contains(normalizedKeyword) ||
                x.Email.Contains(normalizedKeyword) ||
                x.NickName.Contains(normalizedKeyword) ||
                x.RealName.Contains(normalizedKeyword));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var accounts = await query
            .OrderByDescending(x => x.ID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = accounts.Select(x => new AccountListItem(
                x.ID,
                x.No,
                x.LoginUserName,
                string.IsNullOrWhiteSpace(x.NickName) ? "-" : x.NickName,
                x.Phone,
                x.Email,
                x.Status,
                x.AccountType,
                x.LoginTimes,
                x.LastLoginTime,
                dbContext.AccountWallets.Where(w => EF.Property<string>(w, "AccountID") == x.ID).Sum(w => w.Money),
                dbContext.Subscriptions.Count(s => s.AccountId == x.ID),
                dbContext.Orders.Count(o => EF.Property<string>(o, "AccountID") == x.ID)))
            .ToList();

        var enabledStatus = (byte)0;
        var model = new AccountIndexViewModel
        {
            Items = items,
            Keyword = keyword,
            Status = status,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            EnabledCount = await dbContext.Accounts.CountAsync(x => x.Status == enabledStatus, cancellationToken),
            DisabledCount = await dbContext.Accounts.CountAsync(x => x.Status != enabledStatus, cancellationToken),
            WalletTotal = await dbContext.AccountWallets.SumAsync(x => x.Money, cancellationToken),
            SubscriptionCount = await dbContext.Subscriptions.CountAsync(cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> TableData(
        string? keyword,
        long? noMin,
        long? noMax,
        byte? status,
        byte? accountType,
        bool? phoneConfirmed,
        bool? emailConfirmed,
        bool? hasCreator,
        decimal? walletMin,
        decimal? walletMax,
        int? orderMin,
        int? orderMax,
        int? subscriptionMin,
        int? subscriptionMax,
        int? downloadMin,
        int? downloadMax,
        int? loginTimesMin,
        int? loginTimesMax,
        DateTime? lastLoginStart,
        DateTime? lastLoginEnd,
        string? loginIp,
        string? registerIp,
        string? field,
        string? order,
        int page = 1,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        var query = dbContext.Accounts
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            var hasAccountNo = long.TryParse(normalizedKeyword, out var accountNo);
            query = query.Where(x =>
                (hasAccountNo && x.No == accountNo) ||
                x.LoginUserName.Contains(normalizedKeyword) ||
                x.Phone.Contains(normalizedKeyword) ||
                x.Email.Contains(normalizedKeyword) ||
                x.NickName.Contains(normalizedKeyword) ||
                x.RealName.Contains(normalizedKeyword));
        }

        if (noMin is not null)
        {
            query = query.Where(x => x.No >= noMin);
        }

        if (noMax is not null)
        {
            query = query.Where(x => x.No <= noMax);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (accountType is not null)
        {
            query = query.Where(x => x.AccountType == accountType);
        }

        if (phoneConfirmed is not null)
        {
            query = query.Where(x => x.PhoneConfirmed == phoneConfirmed);
        }

        if (emailConfirmed is not null)
        {
            query = query.Where(x => x.EmailConfirmed == emailConfirmed);
        }

        if (hasCreator is true)
        {
            query = query.Where(x => dbContext.CreatorProfiles.Any(c => c.AccountId == x.ID));
        }
        else if (hasCreator is false)
        {
            query = query.Where(x => !dbContext.CreatorProfiles.Any(c => c.AccountId == x.ID));
        }

        if (walletMin is not null)
        {
            query = query.Where(x => (dbContext.AccountWallets
                .Where(w => EF.Property<string>(w, "AccountID") == x.ID)
                .Sum(w => (decimal?)w.Money) ?? 0m) >= walletMin);
        }

        if (walletMax is not null)
        {
            query = query.Where(x => (dbContext.AccountWallets
                .Where(w => EF.Property<string>(w, "AccountID") == x.ID)
                .Sum(w => (decimal?)w.Money) ?? 0m) <= walletMax);
        }

        if (orderMin is not null)
        {
            query = query.Where(x => dbContext.Orders.Count(o => EF.Property<string>(o, "AccountID") == x.ID) >= orderMin);
        }

        if (orderMax is not null)
        {
            query = query.Where(x => dbContext.Orders.Count(o => EF.Property<string>(o, "AccountID") == x.ID) <= orderMax);
        }

        if (subscriptionMin is not null)
        {
            query = query.Where(x => dbContext.Subscriptions.Count(s => s.AccountId == x.ID) >= subscriptionMin);
        }

        if (subscriptionMax is not null)
        {
            query = query.Where(x => dbContext.Subscriptions.Count(s => s.AccountId == x.ID) <= subscriptionMax);
        }

        if (downloadMin is not null)
        {
            query = query.Where(x => dbContext.DownloadRecords.Count(d => d.AccountId == x.ID) >= downloadMin);
        }

        if (downloadMax is not null)
        {
            query = query.Where(x => dbContext.DownloadRecords.Count(d => d.AccountId == x.ID) <= downloadMax);
        }

        if (loginTimesMin is not null)
        {
            query = query.Where(x => x.LoginTimes >= loginTimesMin);
        }

        if (loginTimesMax is not null)
        {
            query = query.Where(x => x.LoginTimes <= loginTimesMax);
        }

        if (lastLoginStart is not null)
        {
            query = query.Where(x => x.LastLoginTime >= lastLoginStart);
        }

        if (lastLoginEnd is not null)
        {
            query = query.Where(x => x.LastLoginTime <= lastLoginEnd);
        }

        if (!string.IsNullOrWhiteSpace(loginIp))
        {
            var normalizedLoginIp = loginIp.Trim();
            query = query.Where(x => x.LoginIP.Contains(normalizedLoginIp));
        }

        if (!string.IsNullOrWhiteSpace(registerIp))
        {
            var normalizedRegisterIp = registerIp.Trim();
            query = query.Where(x => x.RegestorIP.Contains(normalizedRegisterIp));
        }

        var count = await query.CountAsync(cancellationToken);
        var accounts = await ApplyAccountSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var accountIds = accounts.Select(x => x.ID).ToArray();
        var walletBalances = await dbContext.AccountWallets
            .AsNoTracking()
            .Where(x => accountIds.Contains(EF.Property<string>(x, "AccountID")))
            .GroupBy(x => EF.Property<string>(x, "AccountID"))
            .Select(x => new { AccountId = x.Key, Balance = x.Sum(w => w.Money) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance, cancellationToken);
        var subscriptionCounts = await dbContext.Subscriptions
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId))
            .GroupBy(x => x.AccountId)
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var orderCounts = await dbContext.Orders
            .AsNoTracking()
            .Where(x => accountIds.Contains(EF.Property<string>(x, "AccountID")))
            .GroupBy(x => EF.Property<string>(x, "AccountID"))
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var downloadCounts = await dbContext.DownloadRecords
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId))
            .GroupBy(x => x.AccountId)
            .Select(x => new { AccountId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken);
        var creatorRows = await dbContext.CreatorProfiles
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => new { x.AccountId, x.Status })
            .ToListAsync(cancellationToken);
        var creatorStatuses = creatorRows.ToDictionary(x => x.AccountId, x => x.Status.ToString());

        var rows = accounts.Select(x => new AccountTableItem(
                x.ID,
                x.No,
                x.LoginUserName,
                GetDisplayText(x.NickName),
                GetDisplayText(x.RealName),
                GetDisplayText(x.Phone),
                GetDisplayText(x.Email),
                x.Status,
                GetStatusName(x.Status),
                GetStatusBadgeClass(x.Status),
                x.AccountType,
                GetAccountTypeName(x.AccountType),
                x.PhoneConfirmed,
                x.EmailConfirmed,
                x.LoginTimes,
                FormatDate(x.LastLoginTime),
                GetDisplayText(x.LoginIP),
                GetDisplayText(x.RegestorIP),
                walletBalances.GetValueOrDefault(x.ID),
                subscriptionCounts.GetValueOrDefault(x.ID),
                orderCounts.GetValueOrDefault(x.ID),
                downloadCounts.GetValueOrDefault(x.ID),
                creatorStatuses.GetValueOrDefault(x.ID, "-"),
                x.TimeStamp))
            .ToList();

        return Json(new LayuiTableResult<AccountTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Transactions(string accountId, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts
            .AsNoTracking()
            .Where(x => x.ID == accountId)
            .Select(x => new AccountTransactionsViewModel
            {
                AccountId = x.ID,
                LoginUserName = x.LoginUserName
            })
            .FirstOrDefaultAsync(cancellationToken);

        return account is null ? NotFound() : View(account);
    }

    public async Task<IActionResult> TransactionsForAccount(
        string accountId,
        string? keyword,
        byte? payStatus,
        byte? payMethod,
        byte? type,
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
            .Where(x => EF.Property<string>(x, "AccountID") == accountId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.No.Contains(normalizedKeyword) ||
                x.OrderNo.Contains(normalizedKeyword) ||
                (x.ThirdNo != null && x.ThirdNo.Contains(normalizedKeyword)) ||
                x.Title.Contains(normalizedKeyword) ||
                x.Content.Contains(normalizedKeyword));
        }

        if (payStatus is not null)
        {
            query = query.Where(x => x.PayStatus == payStatus);
        }

        if (payMethod is not null)
        {
            query = query.Where(x => x.PayMethod == payMethod);
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
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
            query = query.Where(x => x.PayTime <= payTimeEnd);
        }

        var count = await query.CountAsync(cancellationToken);
        var transactions = await ApplyTransactionSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var orderIds = transactions
            .Select(x => x.OrderId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        var orderNos = transactions
            .Select(x => x.OrderNo)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        var orders = await dbContext.Orders
            .AsNoTracking()
            .Where(x => orderIds.Contains(x.ID) || orderNos.Contains(x.No))
            .Select(x => new { x.ID, x.No, x.AssetId, x.PricingPlanId, x.Status })
            .ToListAsync(cancellationToken);
        var ordersById = orders.ToDictionary(x => x.ID);
        var ordersByNo = orders
            .GroupBy(x => x.No)
            .ToDictionary(x => x.Key, x => x.First());
        var assetIds = orders
            .Select(x => x.AssetId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct()
            .ToArray();
        var pricingPlanIds = orders
            .Select(x => x.PricingPlanId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct()
            .ToArray();
        var assetNames = await dbContext.Assets
            .AsNoTracking()
            .Where(x => assetIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);
        var pricingPlanNames = await dbContext.PricingPlans
            .AsNoTracking()
            .Where(x => pricingPlanIds.Contains(x.ID))
            .ToDictionaryAsync(x => x.ID, x => x.Name, cancellationToken);

        var rows = transactions.Select(x =>
            {
                var relatedOrder = !string.IsNullOrWhiteSpace(x.OrderId) && ordersById.TryGetValue(x.OrderId, out var orderById)
                    ? orderById
                    : ordersByNo.GetValueOrDefault(x.OrderNo);

                return new AccountTransactionTableItem(
                    x.ID,
                    x.No,
                    GetDisplayText(x.OrderNo),
                    GetDisplayText(x.ThirdNo),
                    GetDisplayText(x.Title),
                    relatedOrder is null || string.IsNullOrWhiteSpace(relatedOrder.AssetId) ? "-" : assetNames.GetValueOrDefault(relatedOrder.AssetId, "-"),
                    relatedOrder is null || string.IsNullOrWhiteSpace(relatedOrder.PricingPlanId) ? "-" : pricingPlanNames.GetValueOrDefault(relatedOrder.PricingPlanId, "-"),
                    x.Money,
                    x.RealMoney,
                    x.Type,
                    x.PayStatus,
                    GetPayStatusName(x.PayStatus),
                    x.PayMethod,
                    GetPayMethodName(x.PayMethod),
                    relatedOrder?.Status,
                    relatedOrder is null ? "-" : GetOrderStatusName(relatedOrder.Status),
                    FormatNullableDate(x.PayTime),
                    x.TimeStamp);
            })
            .ToList();

        return Json(new LayuiTableResult<AccountTransactionTableItem>
        {
            Count = count,
            Data = rows
        });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var wallets = await dbContext.AccountWallets
            .AsNoTracking()
            .Where(x => EF.Property<string>(x, "AccountID") == account.ID)
            .Select(x => new AccountWalletListItem(x.Name, x.Money, x.Type, x.Status))
            .ToListAsync(cancellationToken);

        var roles = await dbContext.AccountRolePairs
            .AsNoTracking()
            .Include(x => x.Role)
            .Where(x => EF.Property<string>(x, "AccountID") == account.ID && x.Role != null)
            .Select(x => new AccountRoleListItem(x.Role!.Name, x.Role.Power, x.Role.Enabel))
            .ToListAsync(cancellationToken);

        var properties = await dbContext.AccountPropertys
            .AsNoTracking()
            .Where(x => EF.Property<string>(x, "AccountID") == account.ID)
            .Select(x => new AccountPropertyListItem(x.Key, x.Value, x.Display, x.Group))
            .ToListAsync(cancellationToken);

        var model = new AccountDetailViewModel
        {
            Id = account.ID,
            No = account.No,
            LoginUserName = account.LoginUserName,
            NickName = string.IsNullOrWhiteSpace(account.NickName) ? "-" : account.NickName,
            RealName = string.IsNullOrWhiteSpace(account.RealName) ? "-" : account.RealName,
            Phone = account.Phone,
            Email = account.Email,
            PhoneConfirmed = account.PhoneConfirmed,
            EmailConfirmed = account.EmailConfirmed,
            Status = account.Status,
            AccountType = account.AccountType,
            LoginTimes = account.LoginTimes,
            LastLoginTime = account.LastLoginTime,
            LoginIP = string.IsNullOrWhiteSpace(account.LoginIP) ? "-" : account.LoginIP,
            TimeStamp = account.TimeStamp,
            Wallets = wallets,
            Roles = roles,
            Properties = properties,
            SubscriptionCount = await dbContext.Subscriptions.CountAsync(x => x.AccountId == account.ID, cancellationToken),
            OrderCount = await dbContext.Orders.CountAsync(x => EF.Property<string>(x, "AccountID") == account.ID, cancellationToken),
            DownloadCount = await dbContext.DownloadRecords.CountAsync(x => x.AccountId == account.ID, cancellationToken)
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BatchStatus(string[] ids, byte status, CancellationToken cancellationToken)
    {
        var result = await accountOperations.BatchSetStatusAsync(ids, status, cancellationToken);
        TempData["AdminToast"] = result.Message ?? (result.Success ? "用户状态已批量更新。" : "批量更新用户状态失败。");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new AccountEditViewModel
        {
            Id = account.ID,
            LoginUserName = account.LoginUserName,
            NickName = account.NickName,
            RealName = account.RealName,
            Phone = account.Phone,
            Email = account.Email,
            PhoneConfirmed = account.PhoneConfirmed,
            EmailConfirmed = account.EmailConfirmed
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(AccountEditViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage()));
            }

            return View(model);
        }

        var result = await accountOperations.UpdateProfileAsync(
            model.Id,
            model.NickName,
            model.RealName,
            model.Phone,
            model.Email,
            model.PhoneConfirmed,
            model.EmailConfirmed,
            cancellationToken);
        if (!result.Success)
        {
            var message = result.Message ?? "用户基本信息保存失败。";
            if (IsAjaxRequest())
            {
                return result.ErrorCode == OperationErrorCodes.NotFound
                    ? NotFound(DrawerJson(message))
                    : BadRequest(DrawerJson(message));
            }

            return result.ErrorCode == OperationErrorCodes.NotFound ? NotFound() : BadRequest(message);
        }

        var successMessage = result.Message ?? "用户基本信息已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage);
        }

        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    public async Task<IActionResult> Status(string id, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new AccountStatusViewModel
        {
            Id = account.ID,
            LoginUserName = account.LoginUserName,
            Status = account.Status
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(AccountStatusViewModel model, CancellationToken cancellationToken)
    {
        var result = await accountOperations.SetStatusAsync(model.Id, model.Status, cancellationToken);
        if (!result.Success)
        {
            var message = result.Message ?? "用户状态更新失败。";
            if (IsAjaxRequest())
            {
                return result.ErrorCode == OperationErrorCodes.NotFound
                    ? NotFound(DrawerJson(message))
                    : BadRequest(DrawerJson(message));
            }

            return result.ErrorCode == OperationErrorCodes.NotFound ? NotFound() : BadRequest(message);
        }

        var successMessage = result.Message ?? "用户状态已更新。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage);
        }

        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    public async Task<IActionResult> Recharge(string id, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new AccountRechargeViewModel
        {
            Id = account.ID,
            LoginUserName = account.LoginUserName,
            CurrentBalance = await accountOperations.GetWalletBalanceAsync(account.ID, cancellationToken),
            RequestId = CreateMvcRequestId(),
            Reason = "后台充值"
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recharge(AccountRechargeViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage()));
            }

            model.CurrentBalance = await accountOperations.GetWalletBalanceAsync(model.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(model.RequestId))
            {
                model.RequestId = CreateMvcRequestId();
            }

            return View(model);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var result = await accountOperations.RechargeWalletAsync(
            GetCurrentManagerId(),
            model.Id,
            model.Amount,
            source: "MVC",
            requestId: model.RequestId,
            reason: model.Reason,
            cancellationToken);
        await LogMvcAuditAsync(
            "accounts_recharge_wallet",
            result.Success,
            result.ErrorCode,
            startedAt,
            cancellationToken);
        if (!result.Success)
        {
            var message = result.Message ?? "充值失败，请稍后重试。";
            if (IsAjaxRequest())
            {
                return result.ErrorCode == OperationErrorCodes.NotFound
                    ? NotFound(DrawerJson(message))
                    : BadRequest(DrawerJson(message));
            }

            if (result.ErrorCode == OperationErrorCodes.NotFound)
            {
                return NotFound();
            }

            ModelState.AddModelError(string.Empty, message);
            model.CurrentBalance = await accountOperations.GetWalletBalanceAsync(model.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(model.RequestId))
            {
                model.RequestId = CreateMvcRequestId();
            }

            return View(model);
        }

        var successMessage = result.Message ?? $"已为用户充值 {model.Amount:0.00}。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage);
        }

        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    public async Task<IActionResult> Password(string id, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        return View(new AccountPasswordViewModel
        {
            Id = account.ID,
            LoginUserName = account.LoginUserName
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(AccountPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage()));
            }

            return View(model);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var result = await accountOperations.ResetPasswordAsync(
            model.Id,
            model.NewPassword,
            model.NewSafePassword,
            cancellationToken);
        await LogMvcAuditAsync(
            "accounts_reset_password",
            result.Success,
            result.ErrorCode,
            startedAt,
            cancellationToken);
        if (!result.Success)
        {
            var message = result.Message ?? "用户密码重置失败。";
            if (IsAjaxRequest())
            {
                return result.ErrorCode == OperationErrorCodes.NotFound
                    ? NotFound(DrawerJson(message))
                    : BadRequest(DrawerJson(message));
            }

            return result.ErrorCode == OperationErrorCodes.NotFound ? NotFound() : BadRequest(message);
        }

        var successMessage = result.Message ?? "用户密码已重置。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage);
        }

        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    private static IQueryable<Account> ApplyAccountSorting(IQueryable<Account> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "no" => isAscending ? query.OrderBy(x => x.No) : query.OrderByDescending(x => x.No),
            "loginUserName" => isAscending ? query.OrderBy(x => x.LoginUserName) : query.OrderByDescending(x => x.LoginUserName),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "accountType" => isAscending ? query.OrderBy(x => x.AccountType) : query.OrderByDescending(x => x.AccountType),
            "loginTimes" => isAscending ? query.OrderBy(x => x.LoginTimes) : query.OrderByDescending(x => x.LoginTimes),
            "lastLoginTimeText" => isAscending ? query.OrderBy(x => x.LastLoginTime) : query.OrderByDescending(x => x.LastLoginTime),
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
            "money" => isAscending ? query.OrderBy(x => x.Money) : query.OrderByDescending(x => x.Money),
            "realMoney" => isAscending ? query.OrderBy(x => x.RealMoney) : query.OrderByDescending(x => x.RealMoney),
            "payStatus" => isAscending ? query.OrderBy(x => x.PayStatus) : query.OrderByDescending(x => x.PayStatus),
            "payMethod" => isAscending ? query.OrderBy(x => x.PayMethod) : query.OrderByDescending(x => x.PayMethod),
            "payTimeText" => isAscending ? query.OrderBy(x => x.PayTime) : query.OrderByDescending(x => x.PayTime),
            "timeStamp" => isAscending ? query.OrderBy(x => x.TimeStamp) : query.OrderByDescending(x => x.TimeStamp),
            _ => query.OrderByDescending(x => x.ID)
        };
    }

    private static string GetDisplayText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string GetStatusName(byte status) => status switch
    {
        0 => "正常",
        1 => "禁用",
        2 => "冻结",
        _ => $"状态 {status}"
    };

    private static string GetStatusBadgeClass(byte status) => status switch
    {
        0 => "admin-badge-success",
        1 => "admin-badge-muted",
        2 => "admin-badge-danger",
        _ => "admin-badge-warning"
    };

    private static string GetAccountTypeName(byte type) => type switch
    {
        0 => "普通用户",
        1 => "个人用户",
        2 => "企业用户",
        _ => $"类型 {type}"
    };

    private static string GetPayStatusName(byte status) => status switch
    {
        0 => "未知",
        1 => "待支付",
        2 => "已支付",
        3 => "已取消",
        4 => "失败",
        _ => $"状态 {status}"
    };

    private static string GetPayMethodName(byte method) => method switch
    {
        0 => "未知",
        1 => "余额",
        2 => "微信",
        3 => "支付宝",
        4 => "银行卡",
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

    private static string FormatDate(DateTime value)
    {
        return value == default ? "-" : value.ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatNullableDate(DateTime? value)
    {
        return value is null ? "-" : value.Value.ToString("yyyy-MM-dd HH:mm");
    }

    private bool IsDrawerRequest()
    {
        return string.Equals(Request.Query["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase)
            || (Request.HasFormContentType && string.Equals(Request.Form["mode"].ToString(), "drawer", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers.XRequestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string GetModelStateMessage()
    {
        return ModelState.Values
            .SelectMany(x => x.Errors)
            .Select(x => x.ErrorMessage)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "提交失败，请检查表单内容。";
    }

    private static object DrawerJson(string message, bool success = false, string tableId = "accountTable")
    {
        return new
        {
            success,
            message,
            tableId
        };
    }

    private ContentResult DrawerSuccess(string message, string tableId = "accountTable")
    {
        var encodedMessage = JsonSerializer.Serialize(message);
        var encodedTableId = JsonSerializer.Serialize(tableId);

        var html = "<!DOCTYPE html><html lang=\"zh-CN\"><body><script>"
            + "if (parent && parent.layui) {"
            + $"if (parent.layui.table) parent.layui.table.reload({encodedTableId});"
            + $"if (parent.layui.layer) parent.layui.layer.msg({encodedMessage});"
            + "}"
            + "if (parent && parent.layer && window.name) {"
            + "parent.layer.close(parent.layer.getFrameIndex(window.name));"
            + "} else {"
            + "location.href = document.referrer || \"/Accounts\";"
            + "}"
            + "</script></body></html>";

        return Content(html, "text/html");
    }

    private string GetCurrentManagerId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "admin";

    private async Task LogMvcAuditAsync(
        string toolName,
        bool success,
        string? errorCode,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await auditService.LogAsync(new AdminAuditEntry(
            ManagerId: GetCurrentManagerId(),
            TokenId: null,
            ToolName: toolName,
            Source: "MVC",
            Ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
            Ua: Request.Headers.UserAgent.ToString(),
            Success: success,
            ErrorCode: errorCode,
            DurationMs: (int)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds)),
            cancellationToken);
    }

    private static string CreateMvcRequestId()
        => Guid.NewGuid().ToString("N");
}
