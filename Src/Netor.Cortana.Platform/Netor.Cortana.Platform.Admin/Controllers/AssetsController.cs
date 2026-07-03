using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Models.Assets;
using Netor.Cortana.Platform.Admin.Models.Shared;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Risk;
using Netor.Cortana.Platform.Services.Solutions;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class AssetsController(
    PlatformDbContext dbContext,
    PackageStorageService packageStorageService,
    PackageRiskService packageRiskService) : Controller
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

    public async Task<IActionResult> TableData(
        string? keyword,
        AssetType? type,
        AssetStatus? status,
        string? categoryId,
        bool? featured,
        string? ownerKeyword,
        string? developerName,
        int? downloadMin,
        int? downloadMax,
        int? versionCountMin,
        PricingPlanType? pricingPlanType,
        decimal? priceMin,
        decimal? priceMax,
        bool? hasPackage,
        string? storageProvider,
        AssetReviewStatus? reviewStatus,
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
            .Include(x => x.OwnerAccount)
            .Include(x => x.Category)
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .AsSplitQuery()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(x =>
                x.Name.Contains(normalizedKeyword) ||
                x.Slug.Contains(normalizedKeyword) ||
                x.DeveloperName.Contains(normalizedKeyword) ||
                x.ShortDescription.Contains(normalizedKeyword) ||
                x.Tags.Contains(normalizedKeyword));
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

        if (!string.IsNullOrWhiteSpace(ownerKeyword))
        {
            var normalizedOwner = ownerKeyword.Trim();
            query = query.Where(x => x.OwnerAccount != null && x.OwnerAccount.LoginUserName.Contains(normalizedOwner));
        }

        if (!string.IsNullOrWhiteSpace(developerName))
        {
            var normalizedDeveloper = developerName.Trim();
            query = query.Where(x => x.DeveloperName.Contains(normalizedDeveloper));
        }

        if (downloadMin is not null)
        {
            query = query.Where(x => x.DownloadCount >= downloadMin);
        }

        if (downloadMax is not null)
        {
            query = query.Where(x => x.DownloadCount <= downloadMax);
        }

        if (versionCountMin is not null)
        {
            query = query.Where(x => x.Versions.Count >= versionCountMin);
        }

        if (pricingPlanType is not null)
        {
            query = query.Where(x => x.PricingPlans.Any(p => p.PlanType == pricingPlanType));
        }

        if (priceMin is not null)
        {
            query = query.Where(x => x.PricingPlans.Any(p => p.Price >= priceMin));
        }

        if (priceMax is not null)
        {
            query = query.Where(x => x.PricingPlans.Any(p => p.Price <= priceMax));
        }

        if (hasPackage is true)
        {
            query = query.Where(x => x.Versions.Any(v => v.FilePath != string.Empty));
        }
        else if (hasPackage is false)
        {
            query = query.Where(x => !x.Versions.Any(v => v.FilePath != string.Empty));
        }

        if (!string.IsNullOrWhiteSpace(storageProvider))
        {
            var normalizedProvider = storageProvider.Trim();
            query = query.Where(x => x.Versions.Any(v => v.StorageProvider == normalizedProvider));
        }

        if (reviewStatus is not null)
        {
            query = query.Where(x => dbContext.AssetReviews
                .Where(r => r.AssetId == x.ID)
                .OrderByDescending(r => r.TimeStamp)
                .Select(r => (AssetReviewStatus?)r.Status)
                .FirstOrDefault() == reviewStatus);
        }

        var count = await query.CountAsync(cancellationToken);
        var assets = await ApplyAssetSorting(query, field, order)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var assetIds = assets.Select(x => x.ID).ToArray();
        var latestReviews = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => assetIds.Contains(x.AssetId))
            .GroupBy(x => x.AssetId)
            .Select(x => new
            {
                AssetId = x.Key,
                Status = x.OrderByDescending(r => r.TimeStamp).Select(r => r.Status).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        var latestReviewStatuses = latestReviews.ToDictionary(x => x.AssetId, x => GetReviewStatusName(x.Status));

        var rows = assets.Select(x =>
            {
                var currentVersion = GetCurrentVersion(x);
                return new AssetTableItem(
                    x.ID,
                    x.Name,
                    x.Slug,
                    x.DeveloperName,
                    x.OwnerAccount?.LoginUserName ?? "未绑定",
                    x.ShortDescription,
                    x.Type,
                    GetAssetTypeName(x.Type),
                    x.Status,
                    GetAssetStatusName(x.Status),
                    GetAssetStatusBadgeClass(x.Status),
                    x.Category?.Name ?? "未分类",
                    x.IsFeatured,
                    x.DownloadCount,
                    x.Versions.Count,
                    x.PricingPlans.Count,
                    currentVersion?.VersionName ?? "-",
                    GetPricingSummary(x.PricingPlans),
                    latestReviewStatuses.GetValueOrDefault(x.ID, "-"),
                    x.PublishedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-");
            })
            .ToList();

        return Json(new LayuiTableResult<AssetTableItem>
        {
            Count = count,
            Data = rows
        });
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

        var uploadedPackage = await GetUploadedPackageAsync(model.UploadedPackageToken, cancellationToken);
        if (uploadedPackage is null)
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.UploadedPackageToken), "请先上传资源包。");
        }
        else
        {
            if (uploadedPackage.AssetType != model.Type)
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.Type), "资源类型已变更，请重新上传资源包。");
            }

            if (!string.Equals(uploadedPackage.Slug, model.Slug, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.Slug), "资源标识已变更，请重新上传资源包。");
            }

            if (!string.Equals(uploadedPackage.VersionName, model.VersionName, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.VersionName), "版本号已变更，请重新上传资源包。");
            }
        }

        if (!ModelState.IsValid || uploadedPackage is null || ownerAccount is null)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "assetTable"));
            }

            return View(await CreateAssetCreateViewModelAsync(model, cancellationToken));
        }

        var asset = new Asset
        {
            Name = model.Name,
            Slug = model.Slug,
            Type = model.Type,
            CategoryId = model.CategoryId,
            OwnerAccount = ownerAccount,
            DeveloperName = model.DeveloperName,
            ShortDescription = model.ShortDescription,
            Description = model.Description ?? string.Empty,
            Tags = model.Tags ?? string.Empty,
            IsFeatured = model.IsFeatured,
            Status = model.PublishNow ? AssetStatus.Published : AssetStatus.Draft,
            PublishedAtUtc = model.PublishNow ? DateTimeOffset.UtcNow : null,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = model.VersionName,
                    ReleaseNotes = model.ReleaseNotes ?? string.Empty,
                    ManifestJson = uploadedPackage.ManifestJson,
                    PackageHash = uploadedPackage.PackageHash,
                    StorageProvider = uploadedPackage.StorageProvider,
                    PackageSize = uploadedPackage.PackageSize,
                    FilePath = uploadedPackage.FilePath
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

        const string successMessage = "资源已上传并创建。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "assetTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "assetTable");
        }

        return RedirectToAction(nameof(Details), new { id = asset.ID });
    }

    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        var version = GetCurrentVersion(asset);
        var pricingPlan = asset.PricingPlans
            .Where(x => x.IsActive)
            .OrderBy(x => x.Price)
            .FirstOrDefault()
            ?? asset.PricingPlans.OrderBy(x => x.Price).FirstOrDefault();

        var model = new AssetCreateViewModel
        {
            Id = asset.ID,
            Name = asset.Name,
            Slug = asset.Slug,
            Type = asset.Type,
            CategoryId = asset.CategoryId,
            DeveloperName = asset.DeveloperName,
            ShortDescription = asset.ShortDescription,
            Description = asset.Description,
            Tags = asset.Tags,
            IsFeatured = asset.IsFeatured,
            PublishNow = asset.Status == AssetStatus.Published,
            VersionName = version?.VersionName ?? "1.0.0",
            ReleaseNotes = version?.ReleaseNotes ?? string.Empty,
            UploadedPackageToken = "__existing__",
            UploadedPackageFileName = Path.GetFileName(version?.FilePath ?? string.Empty),
            UploadedPackageSize = version?.PackageSize,
            PlanType = pricingPlan?.PlanType ?? PricingPlanType.Free,
            Price = pricingPlan?.Price ?? 0,
            Currency = pricingPlan?.Currency ?? "CNY",
            DurationDays = pricingPlan?.DurationDays ?? 0
        };

        return View("Create", await CreateAssetCreateViewModelAsync(model, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(AssetCreateViewModel model, CancellationToken cancellationToken)
    {
        NormalizeCreateModel(model);

        if (string.IsNullOrWhiteSpace(model.Id))
        {
            return NotFound();
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

        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .FirstOrDefaultAsync(x => x.ID == model.Id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        var slugExists = await dbContext.Assets
            .AnyAsync(x => x.ID != model.Id && x.Slug == model.Slug, cancellationToken);
        if (slugExists)
        {
            ModelState.AddModelError(nameof(AssetCreateViewModel.Slug), "资源标识已存在");
        }

        var version = GetCurrentVersion(asset);
        if (version is null)
        {
            ModelState.AddModelError(string.Empty, "当前资源缺少可编辑的版本记录。");
        }
        else
        {
            if (!string.Equals(version.VersionName, model.VersionName, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.VersionName), "暂不支持直接修改现有版本号，如需变更版本请重新上传资源包。");
            }

            if (!string.Equals(asset.Slug, model.Slug, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.Slug), "暂不支持直接修改现有资源标识，如需变更标识请重新上传资源包创建新资源。");
            }

            if (asset.Type != model.Type)
            {
                ModelState.AddModelError(nameof(AssetCreateViewModel.Type), "暂不支持直接修改资源类型，如需变更类型请重新上传资源包创建新资源。");
            }
        }

        if (!ModelState.IsValid)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "assetTable"));
            }

            return View("Create", await CreateAssetCreateViewModelAsync(model, cancellationToken));
        }

        asset.Name = model.Name;
        asset.CategoryId = model.CategoryId;
        asset.DeveloperName = model.DeveloperName;
        asset.ShortDescription = model.ShortDescription;
        asset.Description = model.Description ?? string.Empty;
        asset.Tags = model.Tags ?? string.Empty;
        asset.IsFeatured = model.IsFeatured;

        if (model.PublishNow)
        {
            if (!await TryPublishAssetAsync(asset, cancellationToken))
            {
                if (IsAjaxRequest())
                {
                    return BadRequest(DrawerJson("资源未通过发布前校验，请检查资源包、哈希与 Manifest。", "assetTable"));
                }

                ModelState.AddModelError(string.Empty, "资源未通过发布前校验，请检查资源包、哈希与 Manifest。");
                return View("Create", await CreateAssetCreateViewModelAsync(model, cancellationToken));
            }
        }
        else if (asset.Status == AssetStatus.Published)
        {
            asset.Status = AssetStatus.Draft;
        }

        version!.ReleaseNotes = model.ReleaseNotes ?? string.Empty;

        var pricingPlan = asset.PricingPlans
            .Where(x => x.IsActive)
            .OrderBy(x => x.Price)
            .FirstOrDefault()
            ?? asset.PricingPlans.OrderBy(x => x.Price).FirstOrDefault();
        if (pricingPlan is null)
        {
            pricingPlan = new PricingPlan
            {
                Asset = asset,
                IsActive = true
            };
            asset.PricingPlans.Add(pricingPlan);
        }

        pricingPlan.Name = GetDefaultPricingPlanName(model.PlanType);
        pricingPlan.PlanType = model.PlanType;
        pricingPlan.Price = model.Price;
        pricingPlan.Currency = model.Currency;
        pricingPlan.DurationDays = model.DurationDays;
        pricingPlan.IsActive = true;

        await dbContext.SaveChangesAsync(cancellationToken);

        const string successMessage = "资源已保存。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "assetTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "assetTable");
        }

        return RedirectToAction(nameof(Details), new { id = asset.ID });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadPackage(IFormFile? packageFile, AssetType? assetType, CancellationToken cancellationToken)
    {
        if (packageFile is null || packageFile.Length == 0)
        {
            return BadRequest(new { success = false, message = "请选择要上传的资源包。" });
        }

        if (!Path.GetExtension(packageFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "当前只支持上传 .zip 资源包。" });
        }

        var uploadRoot = GetTemporaryUploadRoot();
        Directory.CreateDirectory(uploadRoot);

        var token = Guid.NewGuid().ToString("N");
        var tempPackagePath = Path.Combine(uploadRoot, $"{token}.zip");

        try
        {
            await using (var targetStream = System.IO.File.Create(tempPackagePath))
            await using (var sourceStream = packageFile.OpenReadStream())
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken);
            }

            if (assetType is null)
            {
                return BadRequest(new { success = false, message = "请先选择资源类型后再上传资源包。" });
            }

            var parseResult = await ParseUploadedPackageAsync(tempPackagePath, packageFile.FileName, assetType.Value, cancellationToken);
            var providerAvailability = await packageStorageService.GetCurrentProviderAvailabilityAsync(cancellationToken);
            if (!providerAvailability.IsAvailable)
            {
                return BadRequest(new { success = false, message = providerAvailability.Issue ?? "资源包存储提供方不可用。" });
            }

            await using var persistedPackageStream = System.IO.File.OpenRead(tempPackagePath);
            var packageInfo = await packageStorageService.SavePackageAsync(
                persistedPackageStream,
                parseResult.AssetType,
                parseResult.Slug,
                parseResult.VersionName,
                packageFile.FileName,
                cancellationToken);

            var uploadedPackage = new UploadedPackageRecord(
                token,
                packageFile.FileName,
                packageInfo.RelativePath,
                packageInfo.Hash,
                packageInfo.Size,
                packageInfo.StorageProvider,
                parseResult.AssetType,
                parseResult.Slug,
                parseResult.VersionName,
                parseResult.ManifestJson,
                parseResult.FormData,
                DateTimeOffset.UtcNow);

            await SaveUploadedPackageAsync(uploadedPackage, cancellationToken);
            TryDeleteTemporaryFile(tempPackagePath);

            return Json(new
            {
                success = true,
                message = "资源包上传成功，已解析清单信息。",
                token = uploadedPackage.Token,
                fileName = uploadedPackage.OriginalFileName,
                packageSize = uploadedPackage.PackageSize,
                storageProvider = uploadedPackage.StorageProvider,
                formData = uploadedPackage.FormData
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            TryDeleteTemporaryFile(tempPackagePath);
            return BadRequest(new { success = false, message = ex.Message });
        }
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

        var subscriptions = await dbContext.Subscriptions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.PricingPlan)
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .ToListAsync(cancellationToken);

        var subscriptionIds = subscriptions.Select(x => x.ID).ToArray();
        var downloadCountsBySubscriptionId = subscriptionIds.Length == 0
            ? new Dictionary<string, int>()
            : await dbContext.DownloadRecords
                .AsNoTracking()
                .Where(x => x.SubscriptionId != null && subscriptionIds.Contains(x.SubscriptionId))
                .GroupBy(x => x.SubscriptionId!)
                .Select(x => new { SubscriptionId = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.SubscriptionId, x => x.Count, cancellationToken);

        var recentDownloads = await dbContext.DownloadRecords
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.AssetVersion)
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .Take(8)
            .ToListAsync(cancellationToken);

        var activeSubscriptionCount = subscriptions.Count(x => x.Status == SubscriptionStatus.Active && x.ExpiresAtUtc > DateTimeOffset.UtcNow);
        var subscriberUserCount = subscriptions
            .Select(x => x.AccountId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var currentVersion = asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId);
        var solutionSummary = asset.Type == AssetType.Solution
            ? ParseSolutionSummary(currentVersion?.ManifestJson)
            : null;
        var actualDownloadCount = await dbContext.DownloadRecords.CountAsync(x => x.AssetId == asset.ID, cancellationToken);

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
            Status = asset.Status,
            StatusName = GetAssetStatusName(asset.Status),
            StatusBadgeClass = GetAssetStatusBadgeClass(asset.Status),
            CategoryName = asset.Category?.Name,
            IsFeatured = asset.IsFeatured,
            DownloadCount = actualDownloadCount,
            TotalSubscriptionCount = subscriptions.Count,
            ActiveSubscriptionCount = activeSubscriptionCount,
            SubscriberUserCount = subscriberUserCount,
            VersionCount = asset.Versions.Count,
            PricingPlanCount = asset.PricingPlans.Count,
            CurrentVersionName = currentVersion?.VersionName ?? "-",
            PublishedAtUtc = asset.PublishedAtUtc,
            SolutionSummary = solutionSummary,
            Versions = asset.Versions
                .OrderByDescending(x => x.TimeStamp)
                .Select(x => new AssetVersionItem(
                    x.ID,
                    x.VersionName,
                    x.ReleaseNotes,
                    x.PackageSize,
                    x.FilePath,
                    x.ID == asset.CurrentVersionId,
                    asset.Type == AssetType.Solution ? ParseSolutionSummary(x.ManifestJson) : null))
                .ToList(),
            PricingPlans = asset.PricingPlans
                .OrderBy(x => x.Price)
                .Select(x => new AssetPricingPlanItem(x.Name, GetPricingPlanTypeName(x.PlanType), x.Price, x.Currency, x.DurationDays, x.IsActive))
                .ToList(),
            RecentSubscribers = subscriptions
                .Take(8)
                .Select(x => new AssetSubscriberItem(
                    x.Account?.LoginUserName ?? "未知用户",
                    x.PricingPlan?.Name ?? "未知方案",
                    GetSubscriptionStatusName(x.Status),
                    GetSubscriptionStatusBadgeClass(x.Status),
                    x.StartedAtUtc,
                    x.ExpiresAtUtc,
                    downloadCountsBySubscriptionId.GetValueOrDefault(x.ID)))
                .ToList(),
            RecentDownloads = recentDownloads
                .Select(x => new AssetDownloadUserItem(
                    x.Account?.LoginUserName ?? "未知用户",
                    x.AssetVersion?.VersionName ?? "-",
                    x.IpAddress,
                    DateTimeOffset.FromUnixTimeSeconds(x.TimeStamp)))
                .ToList()
        };

        return View(model);
    }

    public async Task<IActionResult> UploadVersion(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        var currentVersion = GetCurrentVersion(asset);
        var model = new AssetVersionUploadViewModel
        {
            AssetId = asset.ID,
            AssetName = asset.Name,
            AssetSlug = asset.Slug,
            AssetType = asset.Type,
            CurrentVersionName = currentVersion?.VersionName ?? "-",
            VersionName = currentVersion?.VersionName ?? "1.0.0"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadVersion(AssetVersionUploadViewModel model, CancellationToken cancellationToken)
    {
        model.VersionName = model.VersionName?.Trim() ?? string.Empty;
        model.ReleaseNotes = model.ReleaseNotes?.Trim() ?? string.Empty;

        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.ID == model.AssetId, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        model.AssetName = asset.Name;
        model.AssetSlug = asset.Slug;
        model.AssetType = asset.Type;
        model.CurrentVersionName = GetCurrentVersion(asset)?.VersionName ?? "-";

        var uploadedPackage = await GetUploadedPackageAsync(model.UploadedPackageToken, cancellationToken);
        if (uploadedPackage is null)
        {
            ModelState.AddModelError(nameof(AssetVersionUploadViewModel.UploadedPackageToken), "请先上传新版本资源包。");
        }
        else
        {
            if (uploadedPackage.AssetType != asset.Type)
            {
                ModelState.AddModelError(nameof(AssetVersionUploadViewModel.UploadedPackageToken), "资源包类型与当前资源不一致。");
            }

            if (!string.Equals(uploadedPackage.Slug, asset.Slug, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetVersionUploadViewModel.UploadedPackageToken), "资源包标识与当前资源不一致。");
            }

            if (!string.Equals(uploadedPackage.VersionName, model.VersionName, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(AssetVersionUploadViewModel.VersionName), "版本号与已上传资源包解析结果不一致。");
            }
        }

        var versionExists = await dbContext.AssetVersions
            .AnyAsync(x => x.AssetId == model.AssetId && x.VersionName == model.VersionName, cancellationToken);
        if (versionExists)
        {
            ModelState.AddModelError(nameof(AssetVersionUploadViewModel.VersionName), "该版本号已存在，请使用新的版本号。");
        }

        if (!ModelState.IsValid || uploadedPackage is null)
        {
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "assetTable"));
            }

            return View(model);
        }

        var version = new AssetVersion
        {
            AssetId = asset.ID,
            VersionName = model.VersionName,
            ReleaseNotes = model.ReleaseNotes ?? string.Empty,
            ManifestJson = uploadedPackage.ManifestJson,
            PackageHash = uploadedPackage.PackageHash,
            StorageProvider = uploadedPackage.StorageProvider,
            PackageSize = uploadedPackage.PackageSize,
            FilePath = uploadedPackage.FilePath
        };

        asset.Versions.Add(version);
        asset.CurrentVersionId = version.ID;
        asset.PublishedAtUtc ??= DateTimeOffset.UtcNow;

        var integrity = await packageStorageService.VerifyPackageAsync(version, cancellationToken);
        if (integrity.Status != PackageIntegrityStatus.Verified)
        {
            ModelState.AddModelError(nameof(AssetVersionUploadViewModel.UploadedPackageToken), "新版本资源包校验失败，请重新上传后再试。");
            if (IsAjaxRequest())
            {
                return BadRequest(DrawerJson(GetModelStateMessage(), "assetTable"));
            }

            return View(model);
        }

        asset.Status = AssetStatus.Published;
        await dbContext.SaveChangesAsync(cancellationToken);

        const string successMessage = "新版本已上传并生效。";
        if (IsAjaxRequest())
        {
            return Json(DrawerJson(successMessage, "assetTable", true));
        }

        TempData["AdminToast"] = successMessage;
        if (IsDrawerRequest())
        {
            return DrawerSuccess(successMessage, "assetTable");
        }

        return RedirectToAction(nameof(Details), new { id = asset.ID });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Batch(string[] ids, string operation, string? categoryId, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return BatchResult("请先选择要操作的资源。");
        }

        var assets = await dbContext.Assets
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .Where(x => ids.Contains(x.ID))
            .ToListAsync(cancellationToken);

        if (operation == "delete")
        {
            var deletedCount = 0;
            var skippedNotOfflineCount = 0;
            var skippedReferencedCount = 0;

            foreach (var asset in assets)
            {
                if (asset.Status != AssetStatus.Offline)
                {
                    skippedNotOfflineCount++;
                    continue;
                }

                if (await HasAssetDeletionDependenciesAsync(asset.ID, cancellationToken))
                {
                    skippedReferencedCount++;
                    continue;
                }

                if (asset.Versions.Count > 0)
                {
                    dbContext.AssetVersions.RemoveRange(asset.Versions);
                }

                if (asset.PricingPlans.Count > 0)
                {
                    dbContext.PricingPlans.RemoveRange(asset.PricingPlans);
                }

                dbContext.Assets.Remove(asset);
                deletedCount++;
            }

            if (deletedCount > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var messageParts = new List<string> { $"已删除 {deletedCount} 个下架资源。" };
            if (skippedNotOfflineCount > 0)
            {
                messageParts.Add($"{skippedNotOfflineCount} 个资源不是下架状态，已跳过。");
            }

            if (skippedReferencedCount > 0)
            {
                messageParts.Add($"{skippedReferencedCount} 个资源仍有关联订阅、下载、结算或审核记录，已跳过。");
            }

            return BatchResult(string.Join(" ", messageParts), true);
        }

        var failedPublishCount = 0;
        foreach (var asset in assets)
        {
            switch (operation)
            {
                case "publish":
                    if (!await TryPublishAssetAsync(asset, cancellationToken))
                    {
                        failedPublishCount++;
                    }
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
                case "category":
                    asset.CategoryId = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId.Trim();
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var message = failedPublishCount == 0
            ? $"已批量处理 {assets.Count} 个资源。"
            : $"已批量处理 {assets.Count} 个资源，其中 {failedPublishCount} 个资源未通过发布前校验。";
        return BatchResult(message, true);
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
        return OperationResult(asset.IsFeatured ? "推荐状态已更新。" : "推荐状态已取消。");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(string id, CancellationToken cancellationToken)
    {
        var asset = await dbContext.Assets
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.ID == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        if (!await TryPublishAssetAsync(asset, cancellationToken))
        {
            return OperationResult("资源未通过发布前校验，请检查资源包、哈希与 Manifest。", false);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult("资源已发布。");
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
        return OperationResult("资源已下架。");
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

    private static string GetAssetStatusBadgeClass(AssetStatus status) => status switch
    {
        AssetStatus.Published => "admin-badge-success",
        AssetStatus.Hidden => "admin-badge-warning",
        AssetStatus.Offline => "admin-badge-danger",
        _ => "admin-badge-muted"
    };

    private static string GetSubscriptionStatusName(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "有效",
        SubscriptionStatus.Expired => "已过期",
        SubscriptionStatus.Canceled => "已取消",
        _ => status.ToString()
    };

    private static string GetSubscriptionStatusBadgeClass(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "admin-badge-success",
        SubscriptionStatus.Expired => "admin-badge-warning",
        SubscriptionStatus.Canceled => "admin-badge-danger",
        _ => "admin-badge-muted"
    };

    private static string GetReviewStatusName(AssetReviewStatus status) => status switch
    {
        AssetReviewStatus.Pending => "待审核",
        AssetReviewStatus.Approved => "已通过",
        AssetReviewStatus.Rejected => "已驳回",
        AssetReviewStatus.Revoked => "已撤回",
        _ => status.ToString()
    };

    private static IQueryable<Asset> ApplyAssetSorting(IQueryable<Asset> query, string? field, string? order)
    {
        var isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

        return field switch
        {
            "name" => isAscending ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
            "slug" => isAscending ? query.OrderBy(x => x.Slug) : query.OrderByDescending(x => x.Slug),
            "type" => isAscending ? query.OrderBy(x => x.Type) : query.OrderByDescending(x => x.Type),
            "status" => isAscending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "downloadCount" => isAscending ? query.OrderBy(x => x.DownloadCount) : query.OrderByDescending(x => x.DownloadCount),
            "publishedAtText" => isAscending ? query.OrderBy(x => x.PublishedAtUtc) : query.OrderByDescending(x => x.PublishedAtUtc),
            _ => query.OrderByDescending(x => x.TimeStamp).ThenBy(x => x.Name)
        };
    }

    private static AssetVersion? GetCurrentVersion(Asset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.CurrentVersionId))
        {
            var current = asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId);
            if (current is not null)
            {
                return current;
            }
        }

        return asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
    }

    private static string GetPricingSummary(IEnumerable<PricingPlan> pricingPlans)
    {
        var plans = pricingPlans
            .OrderBy(x => x.Price)
            .Select(x => x.PlanType == PricingPlanType.Free ? "免费" : $"{x.Price:0.##} {x.Currency}")
            .Distinct()
            .ToArray();

        return plans.Length == 0 ? "-" : string.Join(" / ", plans);
    }

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

    private static object DrawerJson(string message, string tableId, bool success = false)
    {
        return new
        {
            success,
            message,
            tableId
        };
    }

    private ContentResult DrawerSuccess(string message, string tableId)
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
            + "location.href = document.referrer || \"/Assets\";"
            + "}"
            + "</script></body></html>";

        return Content(html, "text/html");
    }

    private IActionResult BatchResult(string message, bool success = false)
    {
        if (IsAjaxRequest())
        {
            return Json(new { success, message, tableId = "assetTable" });
        }

        TempData["AdminToast"] = message;
        return RedirectToAction(nameof(Index));
    }

    private IActionResult OperationResult(string message, bool success = true)
    {
        if (IsAjaxRequest())
        {
            return Json(new { success, message, tableId = "assetTable" });
        }

        TempData["AdminToast"] = message;
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> HasAssetDeletionDependenciesAsync(string assetId, CancellationToken cancellationToken)
    {
        if (await dbContext.Subscriptions.AnyAsync(x => x.AssetId == assetId, cancellationToken))
        {
            return true;
        }

        if (await dbContext.DownloadRecords.AnyAsync(x => x.AssetId == assetId, cancellationToken))
        {
            return true;
        }

        if (await dbContext.CreatorSettlements.AnyAsync(x => x.AssetId == assetId, cancellationToken))
        {
            return true;
        }

        return await dbContext.AssetReviews.AnyAsync(x => x.AssetId == assetId, cancellationToken);
    }

    private async Task<bool> TryPublishAssetAsync(Asset asset, CancellationToken cancellationToken)
    {
        var version = string.IsNullOrWhiteSpace(asset.CurrentVersionId)
            ? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault()
            : asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId) ?? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
        if (version is null)
        {
            return false;
        }

        var latestReview = await dbContext.AssetReviews
            .AsNoTracking()
            .Where(x => x.AssetId == asset.ID)
            .OrderByDescending(x => x.TimeStamp)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestReview is not null &&
            (latestReview.Status != AssetReviewStatus.Approved || latestReview.AssetVersionId != version.ID))
        {
            return false;
        }

        var risk = packageRiskService.AssessMetadata(version);
        if (!risk.Passed)
        {
            return false;
        }

        var integrity = await packageStorageService.VerifyPackageAsync(version, cancellationToken);
        if (integrity.Status != PackageIntegrityStatus.Verified)
        {
            return false;
        }

        try
        {
            using var packageStream = packageStorageService.OpenPackageRead(version);
            var archiveRisk = packageRiskService.AssessPackageArchive(packageStream, asset.Type);
            if (!archiveRisk.Passed)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        asset.Status = AssetStatus.Published;
        asset.CurrentVersionId ??= version.ID;
        asset.PublishedAtUtc ??= DateTimeOffset.UtcNow;
        return true;
    }

    private static string GetTemporaryUploadRoot()
    {
        return Path.Combine(Path.GetTempPath(), "Netor.Cortana.Platform.Admin", "asset-uploads");
    }

    private static string GetUploadedPackageRecordPath(string token)
    {
        return Path.Combine(GetTemporaryUploadRoot(), $"{token}.json");
    }

    private async Task SaveUploadedPackageAsync(UploadedPackageRecord record, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record);
        await System.IO.File.WriteAllTextAsync(GetUploadedPackageRecordPath(record.Token), json, cancellationToken);
    }

    private async Task<UploadedPackageRecord?> GetUploadedPackageAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var recordPath = GetUploadedPackageRecordPath(token.Trim());
        if (!System.IO.File.Exists(recordPath))
        {
            return null;
        }

        var json = await System.IO.File.ReadAllTextAsync(recordPath, cancellationToken);
        return JsonSerializer.Deserialize<UploadedPackageRecord>(json);
    }

    private static async Task<ParsedPackageResult> ParseUploadedPackageAsync(string zipPath, string originalFileName, AssetType assetType, CancellationToken cancellationToken)
    {
        await using var stream = System.IO.File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("资源包不能为空。");
        }

        var solutionSummary = assetType == AssetType.Solution
            ? SolutionManifestParser.ParseArchive(archive)
            : null;
        var manifestJson = await ReadPackageManifestJsonAsync(archive, originalFileName, assetType, cancellationToken);
        if (solutionSummary is not null)
        {
            manifestJson = SolutionManifestParser.NormalizeManifestJson(manifestJson);
        }

        JsonNode? manifestNode;
        try
        {
            manifestNode = JsonNode.Parse(manifestJson);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("清单 JSON 格式不正确。");
        }

        if (manifestNode is not JsonObject manifestObject)
        {
            throw new InvalidDataException("清单 JSON 必须是对象结构。");
        }

        var name = GetJsonString(manifestObject, "name") ?? Path.GetFileNameWithoutExtension(originalFileName);
        var slug = NormalizeSlug(GetJsonString(manifestObject, "slug"))
            ?? NormalizeSlug(GetJsonString(manifestObject, "id"))
            ?? NormalizeSlug(Path.GetFileNameWithoutExtension(originalFileName))
            ?? throw new InvalidDataException("清单中缺少可用的资源标识。");
        var versionName = GetJsonString(manifestObject, "version")?.Trim();
        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = "1.0.0";
        }

        var shortDescription = GetJsonString(manifestObject, "description")?.Trim()
            ?? GetJsonString(manifestObject, "summary")?.Trim()
            ?? string.Empty;
        var developerName = GetJsonString(manifestObject, "author")?.Trim()
            ?? GetJsonString(manifestObject, "publisher")?.Trim()
            ?? "Netor 官方";
        var tags = ExtractTags(manifestObject);

        var formData = new ParsedPackageFormData(
            name,
            slug,
            assetType,
            developerName,
            shortDescription,
            GetJsonString(manifestObject, "readme")?.Trim() ?? shortDescription,
            tags,
            versionName,
            GetJsonString(manifestObject, "releaseNotes")?.Trim() ?? string.Empty,
            solutionSummary?.CountSummary ?? string.Empty);

        return new ParsedPackageResult(assetType, slug, versionName, manifestJson, formData);
    }

    private static async Task<string> ReadPackageManifestJsonAsync(
        ZipArchive archive,
        string originalFileName,
        AssetType assetType,
        CancellationToken cancellationToken)
    {
        if (assetType == AssetType.Skill)
        {
            var skillEntry = FindEntryByFileName(archive, "skill.md")
                ?? throw new InvalidDataException(GetManifestMissingMessage(assetType));
            var skillMarkdown = await ReadEntryTextAsync(skillEntry, cancellationToken);
            if (string.IsNullOrWhiteSpace(skillMarkdown))
            {
                throw new InvalidDataException("skill.md 不能为空。");
            }

            var skillJsonEntry = FindEntryByFileName(archive, "skill.json");
            if (skillJsonEntry is not null)
            {
                var skillJson = await ReadEntryTextAsync(skillJsonEntry, cancellationToken);
                if (!string.IsNullOrWhiteSpace(skillJson))
                {
                    return skillJson;
                }
            }

            return BuildSkillManifestJson(skillMarkdown, originalFileName);
        }

        var jsonEntry = FindJsonManifestEntry(archive, assetType)
            ?? throw new InvalidDataException(GetManifestMissingMessage(assetType));

        var manifestJson = await ReadEntryTextAsync(jsonEntry, cancellationToken);
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            throw new InvalidDataException("清单 JSON 不能为空。");
        }

        return manifestJson;
    }

    private static ZipArchiveEntry? FindJsonManifestEntry(ZipArchive archive, AssetType assetType)
    {
        foreach (var name in GetManifestFileNames(assetType))
        {
            var entry = FindEntryByFileName(archive, name);
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static ZipArchiveEntry? FindEntryByFileName(ZipArchive archive, string fileName)
        => archive.Entries.FirstOrDefault(x =>
            !x.FullName.EndsWith("/", StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(x.FullName), fileName, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string BuildSkillManifestJson(string skillMarkdown, string originalFileName)
    {
        var lines = skillMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var title = lines
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();

        var description = lines
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0 && !line.StartsWith('#'));

        var name = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalFileName) : title;
        var slug = NormalizeSlug(name)
            ?? NormalizeSlug(Path.GetFileNameWithoutExtension(originalFileName))
            ?? "skill";

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["slug"] = slug,
            ["version"] = "1.0.0",
            ["description"] = description ?? name,
            ["readme"] = skillMarkdown
        };

        return manifest.ToJsonString();
    }

    private static string? GetJsonString(JsonObject manifestObject, string propertyName)
    {
        return manifestObject[propertyName]?.GetValue<string>();
    }

    private static string ExtractTags(JsonObject manifestObject)
    {
        if (manifestObject["tags"] is JsonArray array)
        {
            var values = array
                .Select(x => x?.GetValue<string>()?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return values.Length == 0 ? string.Empty : string.Join(", ", values);
        }

        var tags = GetJsonString(manifestObject, "tags")?.Trim();
        return string.IsNullOrWhiteSpace(tags) ? string.Empty : tags;
    }

    private static SolutionManifestSummary ParseSolutionSummary(string? manifestJson)
        => SolutionManifestParser.ParseManifestJson(manifestJson);

    private static string? NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var chars = normalized
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string[] GetManifestFileNames(AssetType assetType) => assetType switch
    {
        AssetType.Plugin => ["plugin.json"],
        AssetType.Skill => ["skill.md"],
        AssetType.Solution => ["solution.json"],
        AssetType.Agent => ["agent.json"],
        _ => ["manifest.json"]
    };

    private static string GetManifestMissingMessage(AssetType assetType) => assetType switch
    {
        AssetType.Plugin => "插件资源包中未找到 plugin.json。",
        AssetType.Skill => "技能资源包中未找到 skill.md。",
        AssetType.Solution => "解决方案资源包中未找到 solution.json。",
        AssetType.Agent => "智能体资源包中未找到 agent.json。",
        _ => "资源包中未找到可解析的 JSON 清单文件。"
    };
}

public sealed record UploadedPackageRecord(
    string Token,
    string OriginalFileName,
    string FilePath,
    string PackageHash,
    long PackageSize,
    string StorageProvider,
    AssetType AssetType,
    string Slug,
    string VersionName,
    string ManifestJson,
    ParsedPackageFormData FormData,
    DateTimeOffset CreatedAtUtc);

public sealed record ParsedPackageResult(
    AssetType AssetType,
    string Slug,
    string VersionName,
    string ManifestJson,
    ParsedPackageFormData FormData);

public sealed record ParsedPackageFormData(
    string Name,
    string Slug,
    AssetType Type,
    string DeveloperName,
    string ShortDescription,
    string Description,
    string Tags,
    string VersionName,
    string ReleaseNotes,
    string SolutionSummary = "");
