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
                new HomeFeatureItem("插件市场", "扩展本地能力、系统能力和第三方服务。", "插件", Url.Action("Assets", "Market", new { type = AssetType.Plugin }) ?? "/market/assets"),
                new HomeFeatureItem("技能中心", "复用提示词、流程和场景策略。", "技能", Url.Action("Assets", "Market", new { type = AssetType.Skill }) ?? "/market/assets"),
                new HomeFeatureItem("智能体", "封装角色、工具和长期任务流程。", "智能体", Url.Action("Assets", "Market", new { type = AssetType.Agent }) ?? "/market/assets"),
                new HomeFeatureItem("解决方案", "组合插件、技能和智能体的一站式能力包。", "方案", Url.Action("Assets", "Market", new { type = AssetType.Solution }) ?? "/market/assets")
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
