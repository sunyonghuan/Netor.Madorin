using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models;
using Netor.Cortana.Platform.Admin.Models.Dashboard;
using Netor.Cortana.Platform.Entitys.Data;

namespace Netor.Cortana.Platform.Admin.Controllers;

public class HomeController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new DashboardViewModel
        {
            Metrics =
            [
                new DashboardMetric("资源总数", (await dbContext.Assets.CountAsync(cancellationToken)).ToString(), "插件、技能、智能体、解决方案", "资", "#0f766e"),
                new DashboardMetric("个人用户", (await dbContext.Accounts.CountAsync(cancellationToken)).ToString(), "当前平台注册账户", "户", "#1d4ed8"),
                new DashboardMetric("订阅记录", (await dbContext.Subscriptions.CountAsync(cancellationToken)).ToString(), "免费和付费订阅", "订", "#a21caf"),
                new DashboardMetric("销售单", (await dbContext.Orders.CountAsync(cancellationToken)).ToString(), "资源订阅和购买订单", "单", "#ea580c")
            ],
            QuickLinks =
            [
                new DashboardQuickLink("资源管理", "维护插件、技能、智能体和解决方案。", "Assets", "Index", "资"),
                new DashboardQuickLink("分类管理", "维护市场导航、资源分类与展示顺序。", "Categories", "Index", "类"),
                new DashboardQuickLink("订阅管理", "查看订阅状态、有效期和下载权益。", "Subscriptions", "Index", "订"),
                new DashboardQuickLink("订单交易", "查看订单状态、支付结果和交易记录。", "Orders", "Index", "单"),
                new DashboardQuickLink("用户账户", "管理平台用户、账户状态与钱包信息。", "Accounts", "Index", "户"),
                new DashboardQuickLink("系统设置", "维护平台参数、下载策略和关键配置。", "Settings", "Index", "设")
            ]
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
}
