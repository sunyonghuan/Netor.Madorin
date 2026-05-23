using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Web.Models.Home;
using Netor.Cortana.Platform.Web.Models.Market;
using Netor.Cortana.Platform.Web.Models;

namespace Netor.Cortana.Platform.Web.Controllers;

public class HomeController(PlatformDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var featuredAssetRows = await dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.PricingPlans)
            .Where(x => x.Status == AssetStatus.Published && x.IsFeatured)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .Take(6)
            .ToListAsync(cancellationToken);

        var featuredAssets = featuredAssetRows.Select(ToAssetCard).ToList();

        var model = new HomeIndexViewModel
        {
            FeaturedAssets = featuredAssets,
            Features =
            [
                new HomeFeatureItem("插件", "把文件、窗口、Office、运维和外部系统接进 AI。", "PL", Url.Action("Assets", "Market", new { type = AssetType.Plugin }) ?? "/market/assets"),
                new HomeFeatureItem("技能", "沉淀提示词、流程和工作策略，用时即取。", "SK", Url.Action("Assets", "Market", new { type = AssetType.Skill }) ?? "/market/assets"),
                new HomeFeatureItem("智能体", "让不同角色分工协作，不再让一个模型硬撑全场。", "AG", Url.Action("Assets", "Market", new { type = AssetType.Agent }) ?? "/market/assets"),
                new HomeFeatureItem("解决方案", "把插件、技能和智能体打包成可复用的完整工作流。", "SO", Url.Action("Assets", "Market", new { type = AssetType.Solution }) ?? "/market/assets")
            ]
        };

        return View(model);
    }

    public IActionResult About()
    {
        return View();
    }

    public IActionResult Download()
    {
        return View();
    }

    public IActionResult Contact()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    public IActionResult Terms()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static MarketAssetCardViewModel ToAssetCard(Netor.Cortana.Platform.Entitys.Tables.Assets.Asset asset)
    {
        var activePricingPlans = asset.PricingPlans.Where(x => x.IsActive).ToList();

        return new MarketAssetCardViewModel(
            asset.ID,
            asset.Name,
            asset.Slug,
            asset.DeveloperName,
            asset.ShortDescription,
            asset.IconUrl,
            asset.CoverUrl,
            asset.Type,
            GetAssetTypeName(asset.Type),
            asset.Category?.Name,
            asset.IsFeatured,
            asset.DownloadCount,
            asset.TimeStamp,
            activePricingPlans.Count == 0 ? null : activePricingPlans.Min(x => x.Price),
            activePricingPlans.Any(x => x.PlanType == PricingPlanType.Free),
            asset.PublishedAtUtc);
    }
}
