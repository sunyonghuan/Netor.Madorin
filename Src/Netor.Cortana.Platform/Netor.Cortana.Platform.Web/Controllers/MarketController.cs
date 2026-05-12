using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Web.Models.Market;

namespace Netor.Cortana.Platform.Web.Controllers;

[Route("market")]
public sealed class MarketController(PlatformDbContext dbContext) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var categories = await GetCategoriesAsync(cancellationToken);
        var featuredAssets = (await CreateAssetQuery()
            .Where(x => x.IsFeatured)
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .Take(6)
            .ToListAsync(cancellationToken))
            .Select(ToAssetCard)
            .ToList();
        var latestAssets = (await CreateAssetQuery()
            .OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
            .Take(8)
            .ToListAsync(cancellationToken))
            .Select(ToAssetCard)
            .ToList();

        return View(new MarketIndexViewModel
        {
            Categories = categories,
            FeaturedAssets = featuredAssets,
            LatestAssets = latestAssets
        });
    }

    [HttpGet("assets")]
    public async Task<IActionResult> Assets(
        string? keyword,
        AssetType? type,
        string? categoryId,
        string? pricing,
        string? sort,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 12;
        page = Math.Max(1, page);

        var query = dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.PricingPlans)
            .Where(x => x.Status == AssetStatus.Published)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x => x.Name.Contains(normalizedKeyword) || x.ShortDescription.Contains(normalizedKeyword) || x.Tags.Contains(normalizedKeyword));
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        if (string.Equals(pricing, "free", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.PricingPlans.Any(p => p.IsActive && p.PlanType == PricingPlanType.Free));
        }
        else if (string.Equals(pricing, "paid", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.PricingPlans.Any(p => p.IsActive && p.Price > 0));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        query = sort switch
        {
            "downloads" => query.OrderByDescending(x => x.DownloadCount),
            "featured" => query.OrderByDescending(x => x.IsFeatured).ThenByDescending(x => EF.Property<long>(x, "TimeStamp")),
            _ => query.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
        };

        var assetRows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = assetRows.Select(ToAssetCard).ToList();

        return View(new MarketAssetListViewModel
        {
            Keyword = keyword,
            Type = type,
            CategoryId = categoryId,
            Pricing = pricing,
            Sort = sort,
            Page = page,
            TotalPages = totalPages,
            TotalCount = totalCount,
            Categories = await GetCategoriesAsync(cancellationToken),
            Items = items
        });
    }

    [HttpGet("assets/{slug}")]
    public async Task<IActionResult> Details(string slug, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(x => x.Slug == slug && x.Status == AssetStatus.Published, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        return View(new MarketAssetDetailViewModel
        {
            Id = asset.ID,
            Name = asset.Name,
            Slug = asset.Slug,
            DeveloperName = asset.DeveloperName,
            ShortDescription = asset.ShortDescription,
            Description = asset.Description,
            Tags = asset.Tags,
            IconUrl = asset.IconUrl,
            CoverUrl = asset.CoverUrl,
            Type = asset.Type,
            TypeName = GetAssetTypeName(asset.Type),
            CategoryName = asset.Category?.Name,
            DownloadCount = asset.DownloadCount,
            PublishedAtUtc = asset.PublishedAtUtc,
            Versions = asset.Versions
                .OrderByDescending(x => x.TimeStamp)
                .Select(x => new MarketAssetVersionViewModel(x.VersionName, x.ReleaseNotes, x.PackageSize, x.FilePath))
                .ToList(),
            PricingPlans = asset.PricingPlans
                .Where(x => x.IsActive)
                .OrderBy(x => x.Price)
                .Select(x => new MarketPricingPlanViewModel(x.ID, x.Name, x.PlanType, GetPricingPlanTypeName(x.PlanType), x.Price, x.Currency, x.DurationDays, x.IsActive))
                .ToList()
        });
    }

    private IQueryable<Asset> CreateAssetQuery()
    {
        return dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.PricingPlans)
            .Where(x => x.Status == AssetStatus.Published);
    }

    private static MarketAssetCardViewModel ToAssetCard(Asset asset)
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

    private async Task<IReadOnlyList<MarketCategoryViewModel>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Categories
            .AsNoTracking()
            .Where(x => x.IsVisible)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new MarketCategoryViewModel(x.ID, x.Name, x.Slug, x.Description, x.Assets.Count(a => a.Status == AssetStatus.Published)))
            .ToListAsync(cancellationToken);
    }

    private static string GetAssetTypeName(AssetType type) => type switch
    {
        AssetType.Plugin => "插件",
        AssetType.Skill => "技能",
        AssetType.Agent => "智能体",
        AssetType.Solution => "解决方案",
        _ => type.ToString()
    };

    private static string GetPricingPlanTypeName(PricingPlanType type) => type switch
    {
        PricingPlanType.Free => "免费",
        PricingPlanType.Monthly => "月度订阅",
        PricingPlanType.Yearly => "年度订阅",
        _ => type.ToString()
    };
}