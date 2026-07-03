using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models;
using Netor.Cortana.Platform.Admin.Models.Dashboard;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Admin.Controllers;

public class HomeController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var trendStart = today.AddDays(-6);
        using var process = Process.GetCurrentProcess();
        var todayOrderCount = await dbContext.Orders.CountAsync(x => x.PayTime >= today && x.PayTime < tomorrow, cancellationToken);
        var todayIncome = await dbContext.Orders
            .Where(x => x.PayStatus == 2 && x.PayTime >= today && x.PayTime < tomorrow)
            .SumAsync(x => x.Money, cancellationToken);
        var hotAssetRows = await dbContext.Assets
            .AsNoTracking()
            .OrderByDescending(x => x.DownloadCount)
            .ThenBy(x => x.Name)
            .Take(8)
            .ToListAsync(cancellationToken);
        var latestOrderRows = await dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Account)
            .OrderByDescending(x => x.PayTime)
            .ThenByDescending(x => x.TimeStamp)
            .Take(8)
            .ToListAsync(cancellationToken);
        var reviewQueueRows = await dbContext.AssetReviews
            .AsNoTracking()
            .Include(x => x.Asset)
            .Include(x => x.SubmitterAccount)
            .Where(x => x.Status == AssetReviewStatus.Pending)
            .OrderByDescending(x => x.TimeStamp)
            .Take(8)
            .ToListAsync(cancellationToken);
        var trendOrderRows = await dbContext.Orders
            .AsNoTracking()
            .Where(x => x.PayStatus == 2 && x.PayTime >= trendStart && x.PayTime < tomorrow)
            .Select(x => new { x.PayTime, x.Money })
            .ToListAsync(cancellationToken);
        var assetStatusRows = await dbContext.Assets
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() })
            .ToListAsync(cancellationToken);
        var trendGroups = trendOrderRows
            .Where(x => x.PayTime.HasValue)
            .GroupBy(x => x.PayTime!.Value.Date)
            .ToDictionary(
                x => x.Key,
                x => new
                {
                    OrderCount = x.Count(),
                    IncomeAmount = x.Sum(item => item.Money)
                });
        var chartColors = new[] { "#16a085", "#3867d6", "#f6b93b", "#eb3b5a", "#8854d0", "#0fb9b1", "#fa8231", "#4b6584" };
        var uptime = DateTime.Now - process.StartTime;

        var model = new DashboardViewModel
        {
            Metrics =
            [
                new DashboardMetric("资源总数", (await dbContext.Assets.CountAsync(cancellationToken)).ToString(), "插件、技能、智能体、解决方案", "layui-icon-template-1", "#0f766e"),
                new DashboardMetric("待审核资源", (await dbContext.AssetReviews.CountAsync(x => x.Status == AssetReviewStatus.Pending, cancellationToken)).ToString(), "等待平台审核处理", "layui-icon-vercode", "#b45309"),
                new DashboardMetric("用户总数", (await dbContext.Accounts.CountAsync(cancellationToken)).ToString(), "当前平台注册账户", "layui-icon-user", "#1d4ed8"),
                new DashboardMetric("有效订阅", (await dbContext.Subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Active && x.ExpiresAtUtc > DateTimeOffset.UtcNow, cancellationToken)).ToString(), "仍在有效期内的权益", "layui-icon-ok-circle", "#0f766e"),
                new DashboardMetric("今日订单", todayOrderCount.ToString(), "今日完成支付的订单", "layui-icon-cart", "#7c3aed"),
                new DashboardMetric("今日收入", todayIncome.ToString("0.00"), "今日已支付订单金额", "layui-icon-rmb", "#dc2626")
            ],
            QuickLinks =
            [
                new DashboardQuickLink("资源管理", "维护插件、技能、智能体和解决方案。", "Assets", "Index", "layui-icon-template-1"),
                new DashboardQuickLink("审核管理", "处理资源提交、通过、驳回和撤回。", "Reviews", "Index", "layui-icon-vercode"),
                new DashboardQuickLink("用户账户", "管理平台用户、账户状态与钱包信息。", "Accounts", "Index", "layui-icon-user"),
                new DashboardQuickLink("订单交易", "查看订单状态、支付结果和交易记录。", "Orders", "Index", "layui-icon-cart"),
                new DashboardQuickLink("订阅管理", "查看订阅状态、有效期和下载权益。", "Subscriptions", "Index", "layui-icon-template"),
                new DashboardQuickLink("收益结算", "处理创作者收益结算、冻结和驳回。", "Settlements", "Index", "layui-icon-rmb"),
                new DashboardQuickLink("系统设置", "维护平台参数、下载策略和关键配置。", "Settings", "Index", "layui-icon-set")
            ],
            OrderTrend = Enumerable.Range(0, 7)
                .Select(offset => trendStart.AddDays(offset))
                .Select(day =>
                {
                    var exists = trendGroups.TryGetValue(day.Date, out var group);
                    return new DashboardTrendPoint(
                        day.ToString("MM-dd"),
                        exists ? group!.OrderCount : 0,
                        exists ? group!.IncomeAmount : 0);
                })
                .ToList(),
            AssetStatusSegments = assetStatusRows
                .OrderBy(x => x.Status)
                .Select((x, index) => new DashboardChartSegment(GetAssetStatusName(x.Status), x.Count, chartColors[index % chartColors.Length]))
                .ToList(),
            HotAssetDownloadSegments = hotAssetRows
                .Take(6)
                .Select((x, index) => new DashboardChartSegment(x.Name, x.DownloadCount, chartColors[index % chartColors.Length]))
                .ToList(),
            RuntimeInfos =
            [
                new DashboardRuntimeInfo("运行环境", RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, "layui-icon-engine"),
                new DashboardRuntimeInfo("应用进程", process.ProcessName, $"PID {Environment.ProcessId}", "layui-icon-component"),
                new DashboardRuntimeInfo("运行时长", $"{(int)uptime.TotalDays}天 {uptime.Hours}小时", $"{uptime.Minutes} 分钟 {uptime.Seconds} 秒", "layui-icon-time"),
                new DashboardRuntimeInfo("内存占用", $"{process.WorkingSet64 / 1024m / 1024m:0.0} MB", $"托管堆 {GC.GetTotalMemory(false) / 1024m / 1024m:0.0} MB", "layui-icon-chart"),
                new DashboardRuntimeInfo("线程数量", process.Threads.Count.ToString(), Environment.MachineName, "layui-icon-group")
            ],
            HotAssets = hotAssetRows
                .Select(x => new DashboardAssetRankItem(
                    x.ID,
                    x.Name,
                    x.Slug,
                    x.DownloadCount,
                    GetAssetStatusName(x.Status)))
                .ToList(),
            LatestOrders = latestOrderRows
                .Select(x => new DashboardOrderItem(
                    x.ID,
                    x.No,
                    x.Title,
                    x.Account == null ? "未知用户" : x.Account.LoginUserName,
                    x.Money,
                    GetPayStatusName(x.PayStatus),
                    x.PayTime == null ? "-" : x.PayTime.Value.ToString("yyyy-MM-dd HH:mm")))
                .ToList(),
            ReviewQueue = reviewQueueRows
                .Select(x => new DashboardReviewItem(
                    x.ID,
                    x.Asset == null ? "未知资源" : x.Asset.Name,
                    x.SubmitterAccount == null ? "未知账号" : x.SubmitterAccount.LoginUserName,
                    GetReviewStatusName(x.Status),
                    x.TimeStamp.ToString()))
                .ToList()
        };

        return View(model);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static string GetAssetStatusName(AssetStatus status) => status switch
    {
        AssetStatus.Draft => "草稿",
        AssetStatus.Published => "已发布",
        AssetStatus.Hidden => "隐藏",
        AssetStatus.Offline => "下架",
        _ => status.ToString()
    };

    private static string GetPayStatusName(byte status) => status switch
    {
        1 => "待支付",
        2 => "已支付",
        3 => "已取消",
        4 => "失败",
        _ => status == 0 ? "未知" : $"状态 {status}"
    };

    private static string GetReviewStatusName(AssetReviewStatus status) => status switch
    {
        AssetReviewStatus.Pending => "待审核",
        AssetReviewStatus.Approved => "已通过",
        AssetReviewStatus.Rejected => "已驳回",
        AssetReviewStatus.Revoked => "已撤回",
        _ => status.ToString()
    };
}
