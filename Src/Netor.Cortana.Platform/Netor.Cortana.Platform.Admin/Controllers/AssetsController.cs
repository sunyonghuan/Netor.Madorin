using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Assets;
using Netor.Cortana.Platform.Core.Options;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using System.Security.Cryptography;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class AssetsController(PlatformDbContext dbContext, IWebHostEnvironment environment) : Controller
{
    public async Task<IActionResult> Index(
        string? keyword,
        AssetType? type,
        AssetStatus? status,
        string? categoryId,
        bool? featured,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 10;
        page = Math.Max(1, page);

        var query = dbContext.Assets
            .AsNoTracking()
            .Include(x => x.OwnerAccount)
            .Include(x => x.Category)
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x => x.Name.Contains(normalizedKeyword) || x.Slug.Contains(normalizedKeyword) || x.DeveloperName.Contains(normalizedKeyword));
        }

        if (type is not null)
        {
            query = query.Where(x => x.Type == type);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        if (featured is not null)
        {
            query = query.Where(x => x.IsFeatured == featured);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await query
            .OrderByDescending(x => x.TimeStamp)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AssetListItem(
                x.ID,
                x.Name,
                x.Slug,
                x.DeveloperName,
                x.OwnerAccount == null ? null : x.OwnerAccount.LoginUserName,
                x.ShortDescription,
                x.Type,
                GetAssetTypeName(x.Type),
                x.Status,
                GetAssetStatusName(x.Status),
                x.Category == null ? null : x.Category.Name,
                x.IsFeatured,
                x.DownloadCount,
                x.Versions.Count,
                x.PricingPlans.Count,
                x.PublishedAtUtc))
            .ToListAsync(cancellationToken);

        var categories = await dbContext.Categories
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new AssetCategoryOption(x.ID, x.Name))
            .ToListAsync(cancellationToken);

        var model = new AssetIndexViewModel
        {
            Items = items,
            Categories = categories,
            Keyword = keyword,
            Type = type,
            Status = status,
            CategoryId = categoryId,
            Featured = featured,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            PublishedCount = await dbContext.Assets.CountAsync(x => x.Status == AssetStatus.Published, cancellationToken),
            FeaturedCount = await dbContext.Assets.CountAsync(x => x.IsFeatured, cancellationToken)
        };

        return View(model);
    }

    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        return View(await CreateAssetCreateViewModelAsync(new AssetCreateViewModel(), cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> Create(AssetCreateViewModel model, CancellationToken cancellationToken)
    {
        NormalizeCreateModel(model);

        if (model.PackageFile is not null && !Path.GetExtension(model.PackageFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.PackageFile), "当前只支持上传 .zip 资源包");
        }

        if (model.PlanType == PricingPlanType.Free)
        {
            model.Price = 0;
            model.DurationDays = 0;
        }

        if (model.PlanType != PricingPlanType.Free && model.Price <= 0)
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.Price), "付费方案价格必须大于 0");
        }

        if (model.PlanType != PricingPlanType.Free && model.DurationDays <= 0)
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.DurationDays), "付费方案必须设置有效天数");
        }

        var slugExists = await dbContext.Assets.AnyAsync(x => x.Slug == model.Slug, cancellationToken);
        if (slugExists)
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.Slug), "资源标识已存在");
        }

        var ownerAccount = await dbContext.Accounts.FirstOrDefaultAsync(x => x.LoginUserName == "official@netor.me", cancellationToken);
        if (ownerAccount is null)
        {
            ModelState.AddModelError(string.Empty, "官方账号不存在，请先执行数据库初始化。");
        }

        if (!ModelState.IsValid || model.PackageFile is null || ownerAccount is null)
        {
            return View(await CreateAssetCreateViewModelAsync(model, cancellationToken));
        }

        var packageInfo = await SavePackageAsync(model, cancellationToken);
        var asset = new Asset
        {
            Name = model.Name,
            Slug = model.Slug,
            Type = model.Type,
            CategoryId = model.CategoryId,
            OwnerAccount = ownerAccount,
            DeveloperName = model.DeveloperName,
            ShortDescription = model.ShortDescription,
            Description = model.Description,
            Tags = model.Tags,
            IsFeatured = model.IsFeatured,
            Status = model.PublishNow ? AssetStatus.Published : AssetStatus.Draft,
            PublishedAtUtc = model.PublishNow ? DateTimeOffset.UtcNow : null,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = model.VersionName,
                    ReleaseNotes = model.ReleaseNotes ?? string.Empty,
                    ManifestJson = "{}",
                    PackageHash = packageInfo.Hash,
                    PackageSize = packageInfo.Size,
                    FilePath = packageInfo.RelativePath
                }
            ],
            PricingPlans =
            [
                new PricingPlan
                {
                    Name = GetDefaultPricingPlanName(model.PlanType),
                    PlanType = model.PlanType,
                    Price = model.Price,
                    Currency = model.Currency,
                    DurationDays = model.DurationDays,
                    IsActive = true
                }
            ]
        };

        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminToast"] = "资源已上传并创建。";
        return RedirectToAction(nameof(Details), new { id = asset.ID });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(x => x.OwnerAccount)
            .Include(x => x.Category)
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        var model = new AssetDetailViewModel
        {
            Id = asset.ID,
            Name = asset.Name,
            Slug = asset.Slug,
            DeveloperName = asset.DeveloperName,
            OwnerAccountName = asset.OwnerAccount?.LoginUserName,
            ShortDescription = asset.ShortDescription,
            Description = asset.Description,
            Tags = asset.Tags,
            TypeName = GetAssetTypeName(asset.Type),
            StatusName = GetAssetStatusName(asset.Status),
            CategoryName = asset.Category?.Name,
            IsFeatured = asset.IsFeatured,
            DownloadCount = asset.DownloadCount,
            PublishedAtUtc = asset.PublishedAtUtc,
            Versions = asset.Versions
                .OrderByDescending(x => x.TimeStamp)
                .Select(x => new AssetVersionItem(x.VersionName, x.ReleaseNotes, x.PackageSize, x.FilePath))
                .ToList(),
            PricingPlans = asset.PricingPlans
                .OrderBy(x => x.Price)
                .Select(x => new AssetPricingPlanItem(x.Name, GetPricingPlanTypeName(x.PlanType), x.Price, x.Currency, x.DurationDays, x.IsActive))
                .ToList()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Batch(string[] ids, string operation, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            TempData["AdminToast"] = "请先选择要操作的资源。";
            return RedirectToAction(nameof(Index));
        }

        var assets = await dbContext.Assets
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        foreach (var asset in assets)
        {
            switch (operation)
            {
                case "publish":
                    asset.Status = AssetStatus.Published;
                    asset.PublishedAtUtc ??= DateTimeOffset.UtcNow;
                    break;
                case "offline":
                    asset.Status = AssetStatus.Offline;
                    break;
                case "hidden":
                    asset.Status = AssetStatus.Hidden;
                    break;
                case "draft":
                    asset.Status = AssetStatus.Draft;
                    break;
                case "feature":
                    asset.IsFeatured = true;
                    break;
                case "unfeature":
                    asset.IsFeatured = false;
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminToast"] = $"已批量处理 {assets.Count} 个资源。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleFeatured(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        asset.IsFeatured = !asset.IsFeatured;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        asset.Status = AssetStatus.Published;
        asset.PublishedAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Offline(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        asset.Status = AssetStatus.Offline;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

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

    private static string GetPricingPlanTypeName(PricingPlanType type) => type switch
    {
        PricingPlanType.Free => "免费",
        PricingPlanType.Monthly => "月度订阅",
        PricingPlanType.Yearly => "年度订阅",
        _ => type.ToString()
    };

    private static string GetDefaultPricingPlanName(PricingPlanType type) => type switch
    {
        PricingPlanType.Free => "免费版",
        PricingPlanType.Monthly => "月度订阅",
        PricingPlanType.Yearly => "年度订阅",
        _ => GetPricingPlanTypeName(type)
    };

    private async Task<AssetCreateViewModel> CreateAssetCreateViewModelAsync(AssetCreateViewModel model, CancellationToken cancellationToken)
    {
        model.Categories = await dbContext.Categories
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new AssetCategoryOption(x.ID, x.Name))
            .ToListAsync(cancellationToken);

        return model;
    }

    private static void NormalizeCreateModel(AssetCreateViewModel model)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Slug = model.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        model.DeveloperName = model.DeveloperName?.Trim() ?? string.Empty;
        model.ShortDescription = model.ShortDescription?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
        model.Tags = model.Tags?.Trim() ?? string.Empty;
        model.VersionName = model.VersionName?.Trim() ?? string.Empty;
        model.ReleaseNotes = model.ReleaseNotes?.Trim() ?? string.Empty;
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? "CNY" : model.Currency.Trim().ToUpperInvariant();
        model.CategoryId = string.IsNullOrWhiteSpace(model.CategoryId) ? null : model.CategoryId;
    }

    private async Task<PackageSaveResult> SavePackageAsync(AssetCreateViewModel model, CancellationToken cancellationToken)
    {
        var originalFileName = Path.GetFileName(model.PackageFile!.FileName);
        var safeFileName = string.IsNullOrWhiteSpace(originalFileName) ? $"{model.Slug}.zip" : originalFileName;
        var typeFolder = GetAssetTypeFolder(model.Type);
        var relativePath = Path.Combine(new FileStorageOptions().RootPath, "packages", typeFolder, model.Slug, model.VersionName, safeFileName).Replace('\\', '/');
        var physicalPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relativePath));
        var directory = Path.GetDirectoryName(physicalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = System.IO.File.Create(physicalPath))
        {
            await model.PackageFile.CopyToAsync(stream, cancellationToken);
        }

        var hashBytes = await SHA256.HashDataAsync(System.IO.File.OpenRead(physicalPath), cancellationToken);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var size = new FileInfo(physicalPath).Length;
        return new PackageSaveResult(relativePath, hash, size);
    }

    private static string GetAssetTypeFolder(AssetType type) => type switch
    {
        AssetType.Plugin => "plugins",
        AssetType.Skill => "skills",
        AssetType.Agent => "agents",
        AssetType.Solution => "solutions",
        _ => "assets"
    };

    private sealed record PackageSaveResult(string RelativePath, string Hash, long Size);
}
