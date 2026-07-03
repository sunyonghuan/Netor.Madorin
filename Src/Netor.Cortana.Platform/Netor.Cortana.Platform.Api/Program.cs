using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Api.Security;
using Netor.Cortana.Platform.Entitys;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Creators;
using Netor.Cortana.Platform.Services;
using Netor.Cortana.Platform.Services.Auth;
using Netor.Cortana.Platform.Services.Creators;
using Netor.Cortana.Platform.Services.Downloads;
using Netor.Cortana.Platform.Services.Market;
using Netor.Cortana.Platform.Services.Orders;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Subscriptions;

var builder = WebApplication.CreateBuilder(args);
var httpPort = builder.Configuration.GetValue<int?>("Server:HttpPort");
if (httpPort is > 0)
{
	builder.WebHost.UseUrls($"http://*:{httpPort}");
}

builder.Services.AddPlatformDbContext(builder.Configuration);
builder.Services.AddPlatformServices(builder.Configuration);
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
	options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
	options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
	options.SerializerOptions.TypeInfoResolverChain.Insert(0, PlatformApiJsonContext.Default);
});

var app = builder.Build();

await app.InitializeAsync();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new ApiHealthResponse("ok", "Netor.Cortana.Platform.Api")));

api.MapGroup("/market")
	.MapGet("/assets", async (MarketService marketService, CancellationToken cancellationToken) =>
	{
		var assets = await marketService.GetPublishedAssetsAsync(cancellationToken);
		return Results.Ok(assets);
	});

var v1 = api.MapGroup("/v1");

v1.MapPost("/auth/login", async (
	ApiLoginRequest request,
	AuthService authService,
	ApiTokenService tokenService,
	CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
	{
		return Results.Problem("账号和密码不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	var login = await authService.ValidateAccountAsync(request.UserName, request.Password, cancellationToken);
	if (login.Status == AccountLoginStatus.InvalidCredentials)
	{
		return Results.Unauthorized();
	}

	if (login.Status == AccountLoginStatus.AccountDisabled || login.Account is null)
	{
		return Results.Problem("账号已被禁用或冻结。", statusCode: StatusCodes.Status403Forbidden);
	}

	var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);
	var token = tokenService.CreateToken(login.Account.ID, expiresAtUtc);
	return Results.Ok(new ApiLoginResponse(
		token,
		expiresAtUtc,
		new ApiAccountResponse(login.Account.ID, login.Account.No, login.Account.LoginUserName, login.Account.NickName, login.Account.Email, login.Account.Phone)));
});

v1.MapGet("/me", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	return Results.Ok(new ApiAccountResponse(account.ID, account.No, account.LoginUserName, account.NickName, account.Email, account.Phone));
});

v1.MapGet("/assets", async (
	[FromQuery] int page,
	[FromQuery] int pageSize,
	[FromQuery] AssetType? type,
	PlatformDbContext dbContext,
	CancellationToken cancellationToken) =>
{
	page = Math.Max(1, page <= 0 ? 1 : page);
	pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);

	var query = dbContext.Assets
		.AsNoTracking()
		.Include(x => x.Category)
		.Include(x => x.PricingPlans)
		.Include(x => x.Versions)
		.Where(x => x.Status == AssetStatus.Published);

	if (type is not null)
	{
		query = query.Where(x => x.Type == type);
	}

	var totalCount = await query.CountAsync(cancellationToken);

	var assetRows = await query
		.OrderByDescending(x => x.IsFeatured)
		.ThenByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.Skip((page - 1) * pageSize)
		.Take(pageSize)
		.ToListAsync(cancellationToken);

	var assets = assetRows
		.Select(x =>
		{
			var currentVersion = GetCurrentVersion(x.CurrentVersionId, x.Versions);
			return new ApiAssetListItem(
				x.ID,
				x.Slug,
				x.Type,
				x.Name,
				x.DeveloperName,
				x.ShortDescription,
				x.IconUrl,
				x.CoverUrl,
				x.Category == null ? null : x.Category.Name,
				x.IsFeatured,
				x.DownloadCount,
				x.PricingPlans.Any(p => p.IsActive && (p.PlanType == PricingPlanType.Free || p.Price <= 0)),
				x.PricingPlans.Where(p => p.IsActive).Select(p => (decimal?)p.Price).Min(),
				x.CurrentVersionId,
				currentVersion?.VersionName);
		})
		.ToList();

	return Results.Ok(new ApiAssetPageResponse(
		assets,
		page,
		pageSize,
		totalCount,
		page * pageSize < totalCount));
});

v1.MapGet("/assets/{idOrSlug}", async (string idOrSlug, PlatformDbContext dbContext, CancellationToken cancellationToken) =>
{
	var asset = await dbContext.Assets
		.AsNoTracking()
		.Include(x => x.Category)
		.Include(x => x.PricingPlans)
		.Include(x => x.Versions)
		.FirstOrDefaultAsync(x => x.Status == AssetStatus.Published && (x.ID == idOrSlug || x.Slug == idOrSlug), cancellationToken);

	if (asset is null)
	{
		return Results.NotFound();
	}

	var currentVersion = GetCurrentVersion(asset.CurrentVersionId, asset.Versions);
	return Results.Ok(new ApiAssetDetail(
		asset.ID,
		asset.Slug,
		asset.Type,
		asset.Name,
		asset.DeveloperName,
		asset.ShortDescription,
		asset.Description,
		asset.Tags,
		asset.IconUrl,
		asset.CoverUrl,
		asset.Category?.Name,
		asset.DownloadCount,
		currentVersion is null ? null : ToApiVersion(currentVersion),
		asset.PricingPlans.Where(x => x.IsActive).OrderBy(x => x.Price).Select(ToApiPricingPlan).ToList()));
});

v1.MapGet("/assets/{assetId}/download", async (
	string assetId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	DownloadService downloadService,
	CancellationToken cancellationToken) =>
{
	return await PrepareAssetDownloadAsync(assetId, httpContext, dbContext, tokenService, downloadService, cancellationToken);
});

v1.MapPost("/assets/{assetId}/downloads", async (
	string assetId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	DownloadService downloadService,
	CancellationToken cancellationToken) =>
{
	return await PrepareAssetDownloadAsync(assetId, httpContext, dbContext, tokenService, downloadService, cancellationToken);
});

v1.MapGet("/entitlements", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var now = DateTimeOffset.UtcNow;
	var rows = await dbContext.Subscriptions
		.AsNoTracking()
		.Include(x => x.Asset)
		.Include(x => x.PricingPlan)
		.Where(x => x.AccountId == account.ID && x.Status == SubscriptionStatus.Active)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.ToListAsync(cancellationToken);

	var entitlements = rows
		.Where(x => x.Asset is not null && x.Asset.Status == AssetStatus.Published && x.ExpiresAtUtc > now)
		.Select(x => new ApiEntitlement(
			x.ID,
			x.AssetId,
			x.Asset!.Slug,
			x.Asset.Type,
			x.Asset.Name,
			x.PricingPlan?.Name,
			x.StartedAtUtc,
			x.ExpiresAtUtc))
		.ToList();

	return Results.Ok(entitlements);
});

v1.MapGet("/client/install/{assetIdOrSlug}", async (
	string assetIdOrSlug,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	SubscriptionService subscriptionService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var asset = await dbContext.Assets
		.Include(x => x.Versions)
		.Include(x => x.PricingPlans)
		.FirstOrDefaultAsync(x => x.Status == AssetStatus.Published && (x.ID == assetIdOrSlug || x.Slug == assetIdOrSlug), cancellationToken);
	if (asset is null)
	{
		return Results.NotFound();
	}

	var allowed = await EnsureInstallEntitlementAsync(dbContext, subscriptionService, account.ID, asset, cancellationToken);
	if (!allowed)
	{
		return Results.Problem("当前账号没有该资源的有效订阅。", statusCode: StatusCodes.Status403Forbidden);
	}

	var version = GetCurrentVersion(asset.CurrentVersionId, asset.Versions);
	return version is null
		? Results.NotFound()
		: Results.Ok(ToApiClientInstallManifest(asset, version));
});

v1.MapGet("/client/sync", async (
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var now = DateTimeOffset.UtcNow;
	var subscriptions = await dbContext.Subscriptions
		.AsNoTracking()
		.Include(x => x.Asset)
			.ThenInclude(x => x!.Versions)
		.Where(x => x.AccountId == account.ID && x.Status == SubscriptionStatus.Active)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.ToListAsync(cancellationToken);

	var manifests = subscriptions
		.Where(x => x.ExpiresAtUtc > now && x.Asset is { Status: AssetStatus.Published })
		.Select(x =>
		{
			var asset = x.Asset!;
			var version = GetCurrentVersion(asset.CurrentVersionId, asset.Versions);
			return version is null ? null : ToApiClientInstallManifest(asset, version);
		})
		.Where(x => x is not null)
		.Cast<ApiClientInstallManifest>()
		.ToList();

	return Results.Ok(new ApiClientSyncResponse(
		new ApiAccountResponse(account.ID, account.No, account.LoginUserName, account.NickName, account.Email, account.Phone),
		manifests));
});

v1.MapPost("/client/updates", async (
	ApiClientUpdateCheckRequest request,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	SubscriptionService subscriptionService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	if (request.InstalledAssets.Count == 0)
	{
		return Results.Ok(new ApiClientUpdateCheckResponse([]));
	}

	var identifiers = request.InstalledAssets
		.SelectMany(x => new[] { x.AssetId, x.AssetSlug })
		.Where(x => !string.IsNullOrWhiteSpace(x))
		.Select(x => x!)
		.ToHashSet(StringComparer.OrdinalIgnoreCase);

	var assets = await dbContext.Assets
		.Include(x => x.Versions)
		.Include(x => x.PricingPlans)
		.Where(x => x.Status == AssetStatus.Published && (identifiers.Contains(x.ID) || identifiers.Contains(x.Slug)))
		.ToListAsync(cancellationToken);

	var updates = new List<ApiClientUpdateItem>();
	foreach (var installed in request.InstalledAssets)
	{
		var asset = assets.FirstOrDefault(x =>
			(!string.IsNullOrWhiteSpace(installed.AssetId) && x.ID == installed.AssetId)
			|| (!string.IsNullOrWhiteSpace(installed.AssetSlug) && string.Equals(x.Slug, installed.AssetSlug, StringComparison.OrdinalIgnoreCase)));
		if (asset is null)
		{
			continue;
		}

		var allowed = await EnsureInstallEntitlementAsync(dbContext, subscriptionService, account.ID, asset, cancellationToken);
		if (!allowed)
		{
			continue;
		}

		var currentVersion = GetCurrentVersion(asset.CurrentVersionId, asset.Versions);
		if (currentVersion is null)
		{
			continue;
		}

		var sameVersionName =
			!string.IsNullOrWhiteSpace(installed.VersionName)
			&& string.Equals(currentVersion.VersionName, installed.VersionName, StringComparison.OrdinalIgnoreCase);
		var hasUpdate = !sameVersionName
			&& (
				!string.Equals(currentVersion.VersionName, installed.VersionName, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(currentVersion.ID, installed.VersionId, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(currentVersion.PackageHash, installed.PackageHash, StringComparison.OrdinalIgnoreCase));
		if (hasUpdate)
		{
			updates.Add(new ApiClientUpdateItem(
				installed.AssetId,
				installed.AssetSlug,
				installed.VersionId,
				installed.VersionName,
				ToApiClientInstallManifest(asset, currentVersion)));
		}
	}

	return Results.Ok(new ApiClientUpdateCheckResponse(updates));
});

v1.MapPost("/creator/apply", async (
	ApiCreatorApplyRequest request,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CreatorService creatorService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(request.DisplayName))
	{
		return Results.Problem("创作者名称不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	var profile = await creatorService.ApplyAsync(account.ID, request.DisplayName, request.Bio ?? string.Empty, cancellationToken);
	return Results.Ok(ToApiCreatorProfile(profile));
});

v1.MapGet("/creator/profile", async (
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var profile = await dbContext.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == account.ID, cancellationToken);
	return profile is null ? Results.NotFound() : Results.Ok(ToApiCreatorProfile(profile));
});

v1.MapPost("/creator/packages", async (
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	PackageStorageService packageStorageService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var creatorApproved = await dbContext.CreatorProfiles
		.AsNoTracking()
		.AnyAsync(x => x.AccountId == account.ID && x.Status == CreatorStatus.Approved, cancellationToken);
	if (!creatorApproved)
	{
		return Results.Problem("创作者尚未通过审核，不能上传资源包。", statusCode: StatusCodes.Status403Forbidden);
	}

	if (!httpContext.Request.HasFormContentType)
	{
		return Results.Problem("请使用 multipart/form-data 上传资源包。", statusCode: StatusCodes.Status400BadRequest);
	}

	var form = await httpContext.Request.ReadFormAsync(cancellationToken);
	var packageFile = form.Files["package"];
	if (packageFile is null || packageFile.Length == 0)
	{
		return Results.Problem("资源包文件不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	if (!Path.GetExtension(packageFile.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
	{
		return Results.Problem("当前只支持上传 .zip 资源包。", statusCode: StatusCodes.Status400BadRequest);
	}

	if (!TryReadAssetType(form["type"].ToString(), out var assetType))
	{
		return Results.Problem("资源类型不正确。", statusCode: StatusCodes.Status400BadRequest);
	}

	var assetSlug = form["slug"].ToString().Trim().ToLowerInvariant();
	if (!IsValidAssetSlug(assetSlug))
	{
		return Results.Problem("资源标识只能包含小写字母、数字和连字符，并且必须以字母或数字开头。", statusCode: StatusCodes.Status400BadRequest);
	}

	var versionName = form["versionName"].ToString().Trim();
	if (string.IsNullOrWhiteSpace(versionName))
	{
		versionName = "1.0.0";
	}

	var providerAvailability = await packageStorageService.GetCurrentProviderAvailabilityAsync(cancellationToken);
	if (!providerAvailability.IsAvailable)
	{
		return Results.Problem(providerAvailability.Issue ?? "资源包存储提供方不可用。", statusCode: StatusCodes.Status400BadRequest);
	}

	await using var packageStream = packageFile.OpenReadStream();
	PackageSaveResult packageInfo;
	try
	{
		packageInfo = await packageStorageService.SavePackageAsync(
			packageStream,
			assetType,
			assetSlug,
			versionName,
			packageFile.FileName,
			cancellationToken);
	}
	catch (InvalidOperationException ex)
	{
		return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
	}

	return Results.Ok(new ApiCreatorPackageUploadResponse(
		packageInfo.RelativePath,
		packageInfo.Hash,
		packageInfo.Size,
		"SHA256",
		packageInfo.StorageProvider,
		packageFile.FileName));
}).WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));

v1.MapPost("/creator/assets", async (
	ApiCreatorAssetSubmitRequest request,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CreatorService creatorService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug) || string.IsNullOrWhiteSpace(request.FilePath))
	{
		return Results.Problem("资源名称、标识和包路径不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	var normalizedSlug = request.Slug.Trim().ToLowerInvariant();
	if (!IsValidAssetSlug(normalizedSlug))
	{
		return Results.Problem("资源标识只能包含小写字母、数字和连字符，并且必须以字母或数字开头。", statusCode: StatusCodes.Status400BadRequest);
	}

	var result = await creatorService.SubmitAssetAsync(
		account.ID,
		new CreatorAssetSubmission(
			request.Type,
			request.CategoryId,
			request.Name.Trim(),
			normalizedSlug,
			request.ShortDescription?.Trim() ?? string.Empty,
			request.Description?.Trim() ?? string.Empty,
			request.Tags?.Trim() ?? string.Empty,
			string.IsNullOrWhiteSpace(request.VersionName) ? "1.0.0" : request.VersionName.Trim(),
			request.ReleaseNotes?.Trim() ?? string.Empty,
			string.IsNullOrWhiteSpace(request.ManifestJson) ? "{}" : request.ManifestJson,
			request.PackageHash?.Trim() ?? string.Empty,
			string.IsNullOrWhiteSpace(request.StorageProvider) ? "Local" : request.StorageProvider.Trim(),
			request.PackageSize,
			request.FilePath.Trim(),
			request.Price,
			string.IsNullOrWhiteSpace(request.Currency) ? "CNY" : request.Currency.Trim().ToUpperInvariant(),
			request.DurationDays),
		cancellationToken);

	return result.Status switch
	{
		CreatorAssetSubmissionStatus.Created => Results.Ok(new ApiCreatorAssetSubmitResponse(result.AssetId!, result.AssetVersionId!, result.ReviewId!)),
		CreatorAssetSubmissionStatus.CreatorNotApproved => Results.Problem("创作者尚未通过审核，不能提交资源。", statusCode: StatusCodes.Status403Forbidden),
		CreatorAssetSubmissionStatus.SlugExists => Results.Problem("资源标识已存在。", statusCode: StatusCodes.Status409Conflict),
		CreatorAssetSubmissionStatus.InvalidPackageReference => Results.Problem("资源包路径必须与资源类型、标识和版本匹配。", statusCode: StatusCodes.Status400BadRequest),
		CreatorAssetSubmissionStatus.InvalidStorageProvider => Results.Problem("资源包存储提供方不可用。", statusCode: StatusCodes.Status400BadRequest),
		_ => Results.Problem("资源提交失败。", statusCode: StatusCodes.Status400BadRequest)
	};
});

v1.MapGet("/creator/assets", async (
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var assets = await dbContext.Assets
		.AsNoTracking()
		.Include(x => x.Versions)
		.Where(x => x.OwnerAccountId == account.ID)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.ToListAsync(cancellationToken);
	var assetIds = assets.Select(x => x.ID).ToList();
	var reviewRows = await dbContext.AssetReviews
		.AsNoTracking()
		.Where(x => assetIds.Contains(x.AssetId))
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.ToListAsync(cancellationToken);
	var reviews = reviewRows
		.GroupBy(x => x.AssetId)
		.ToDictionary(x => x.Key, x => x.First());

	return Results.Ok(assets.Select(x =>
	{
		var version = GetCurrentVersion(x.CurrentVersionId, x.Versions) ?? x.Versions.OrderByDescending(v => v.TimeStamp).FirstOrDefault();
		reviews.TryGetValue(x.ID, out var review);
		return new ApiCreatorAssetItem(
			x.ID,
			x.Slug,
			x.Type,
			x.Name,
			x.Status,
			version?.VersionName,
			review?.Status,
			review?.Notes,
			x.PublishedAtUtc);
	}).ToList());
});

v1.MapGet("/creator/settlements", async (
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var settlements = await dbContext.CreatorSettlements
		.AsNoTracking()
		.Include(x => x.Asset)
		.Where(x => x.CreatorAccountId == account.ID)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.Select(x => new ApiCreatorSettlementItem(
			x.ID,
			x.AssetId,
			x.Asset == null ? "未知资源" : x.Asset.Name,
			x.OrderId,
			x.GrossAmount,
			x.PlatformFeeAmount,
			x.NetAmount,
			x.Currency,
			x.Status,
			x.Notes,
			x.SettledAtUtc))
		.ToListAsync(cancellationToken);

	return Results.Ok(settlements);
});

v1.MapGet("/me/subscriptions", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	return Results.Ok(await GetSubscriptionItemsAsync(dbContext, account.ID, cancellationToken));
});

v1.MapPost("/subscriptions/{subscriptionId}/cancel", async (
	string subscriptionId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	SubscriptionService subscriptionService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var canceled = await subscriptionService.CancelAsync(account.ID, subscriptionId, cancellationToken);
	return canceled ? Results.Ok(new ApiCancelSubscriptionResponse(subscriptionId, true)) : Results.NotFound();
});

v1.MapGet("/me/downloads", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var downloads = await dbContext.DownloadRecords
		.AsNoTracking()
		.Include(x => x.Asset)
		.Include(x => x.AssetVersion)
		.Where(x => x.AccountId == account.ID)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.Select(x => new ApiDownloadRecord(
			x.ID,
			x.AssetId,
			x.Asset == null ? null : x.Asset.Slug,
			x.Asset == null ? "未知资源" : x.Asset.Name,
			x.AssetVersionId,
			x.AssetVersion == null ? "未知版本" : x.AssetVersion.VersionName,
			x.IpAddress,
			EF.Property<long>(x, "TimeStamp")))
		.ToListAsync(cancellationToken);

	return Results.Ok(downloads);
});

v1.MapPost("/assets/{assetId}/subscribe", async (
	string assetId,
	ApiCreateSubscriptionRequest request,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	OrderService orderService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(request.PricingPlanId))
	{
		return Results.Problem("订阅方案不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	var result = await orderService.CreateAsync(account.ID, assetId, request.PricingPlanId, cancellationToken);
	return result.Status switch
	{
		CreateOrderStatus.FreeSubscriptionCreated => Results.Ok(new ApiSubscribeResponse(true, result.AssetId, null, "free-subscription-created")),
		CreateOrderStatus.PendingOrderCreated => Results.Ok(new ApiSubscribeResponse(false, null, result.OrderId, "pending-order-created")),
		CreateOrderStatus.AccountDisabled => Results.Problem("账号已被禁用或冻结。", statusCode: StatusCodes.Status403Forbidden),
		_ => Results.NotFound()
	};
});

v1.MapPost("/orders", async (
	ApiCreateOrderRequest request,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	OrderService orderService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(request.AssetId) || string.IsNullOrWhiteSpace(request.PricingPlanId))
	{
		return Results.Problem("资源和订阅方案不能为空。", statusCode: StatusCodes.Status400BadRequest);
	}

	var result = await orderService.CreateAsync(account.ID, request.AssetId, request.PricingPlanId, cancellationToken);
	return result.Status switch
	{
		CreateOrderStatus.FreeSubscriptionCreated => Results.Ok(new ApiSubscribeResponse(true, result.AssetId, null, "free-subscription-created")),
		CreateOrderStatus.PendingOrderCreated => Results.Ok(new ApiSubscribeResponse(false, null, result.OrderId, "pending-order-created")),
		CreateOrderStatus.AccountDisabled => Results.Problem("账号已被禁用或冻结。", statusCode: StatusCodes.Status403Forbidden),
		_ => Results.NotFound()
	};
});

v1.MapGet("/me/orders", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	return Results.Ok(await GetOrderItemsAsync(dbContext, account.ID, cancellationToken));
});

v1.MapGet("/orders", async (HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	return Results.Ok(await GetOrderItemsAsync(dbContext, account.ID, cancellationToken));
});

v1.MapGet("/orders/{orderId}", async (string orderId, HttpContext httpContext, PlatformDbContext dbContext, ApiTokenService tokenService, CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var order = await dbContext.Orders
		.AsNoTracking()
		.Where(x => x.ID == orderId && EF.Property<string>(x, "AccountID") == account.ID)
		.Select(x => new ApiOrderDetail(x.ID, x.No, x.Title, x.Content, x.AssetId, x.PricingPlanId, x.Money, x.Numbers, x.PayStatus, x.PayMethod, x.PayStatus == 2))
		.FirstOrDefaultAsync(cancellationToken);

	return order is null ? Results.NotFound() : Results.Ok(order);
});

v1.MapPost("/orders/{orderId}/pay", async (
	string orderId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	OrderService orderService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var result = await orderService.PayAsync(account.ID, orderId, cancellationToken);
	if (!result.Found)
	{
		return Results.NotFound();
	}

	if (result.IsAccountDisabled)
	{
		return Results.Problem("账号已被禁用或冻结。", statusCode: StatusCodes.Status403Forbidden);
	}

	var payment = await orderService.GetPaymentInfoAsync(account.ID, orderId, cancellationToken);
	return Results.Ok(new ApiPayOrderResponse(orderId, payment?.OrderNo, payment?.Amount, payment?.IsPaid ?? true));
});

v1.MapGet("/skills/{assetId}/manifest", async (
	string assetId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	SubscriptionService subscriptionService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var asset = await dbContext.Assets
		.Include(x => x.Versions)
		.Include(x => x.PricingPlans)
		.FirstOrDefaultAsync(x => x.ID == assetId && x.Type == AssetType.Skill && x.Status == AssetStatus.Published, cancellationToken);

	if (asset is null)
	{
		return Results.NotFound();
	}

	var allowed = await HasActiveEntitlementAsync(dbContext, account.ID, asset.ID, cancellationToken);
	if (!allowed)
	{
		var freePlan = asset.PricingPlans.FirstOrDefault(x => x.IsActive && (x.PlanType == PricingPlanType.Free || x.Price <= 0));
		if (freePlan is null)
		{
			return Results.Problem("当前账号没有该技能资源的有效订阅。", statusCode: StatusCodes.Status403Forbidden);
		}

		await subscriptionService.EnsureActiveSubscriptionAsync(account.ID, asset.ID, freePlan, cancellationToken: cancellationToken);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	var version = GetCurrentVersion(asset.CurrentVersionId, asset.Versions);
	if (version is null)
	{
		return Results.NotFound();
	}

	JsonElement? manifest = null;
	if (!string.IsNullOrWhiteSpace(version.ManifestJson))
	{
		manifest = JsonSerializer.Deserialize(version.ManifestJson, PlatformApiJsonContext.Default.JsonElement);
	}

	return Results.Ok(new ApiSkillManifest(asset.ID, asset.Slug, asset.Name, ToApiVersion(version), manifest));
});

v1.MapGet("/skills/{assetId}/download", async (
	string assetId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	DownloadService downloadService,
	CancellationToken cancellationToken) =>
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var isSkill = await dbContext.Assets
		.AsNoTracking()
		.AnyAsync(x => x.ID == assetId && x.Type == AssetType.Skill && x.Status == AssetStatus.Published, cancellationToken);
	if (!isSkill)
	{
		return Results.NotFound();
	}

	return await PrepareAssetDownloadAsync(assetId, httpContext, dbContext, tokenService, downloadService, cancellationToken);
});

app.Run();

static async Task<Netor.Cortana.Platform.Entitys.Tables.Accounts.Account?> GetCurrentAccountAsync(
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	CancellationToken cancellationToken)
{
	var token = ReadBearerToken(httpContext);
	if (string.IsNullOrWhiteSpace(token) || !tokenService.TryValidateToken(token, out var accountId))
	{
		return null;
	}

	return await dbContext.Accounts
		.AsNoTracking()
		.FirstOrDefaultAsync(x => x.ID == accountId && x.Status == 0, cancellationToken);
}

static string? ReadBearerToken(HttpContext httpContext)
{
	var authorization = httpContext.Request.Headers.Authorization.ToString();
	const string prefix = "Bearer ";
	return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
		? authorization[prefix.Length..].Trim()
		: null;
}

static bool TryReadAssetType(string value, out AssetType assetType)
{
	if (int.TryParse(value, out var numericValue) && Enum.IsDefined(typeof(AssetType), numericValue))
	{
		assetType = (AssetType)numericValue;
		return true;
	}

	return Enum.TryParse(value, ignoreCase: true, out assetType);
}

static bool IsValidAssetSlug(string slug)
{
	if (string.IsNullOrWhiteSpace(slug) || !char.IsLetterOrDigit(slug[0]))
	{
		return false;
	}

	return slug.All(ch => char.IsAsciiLetterLower(ch) || char.IsDigit(ch) || ch == '-');
}

static async Task<IResult> PrepareAssetDownloadAsync(
	string assetId,
	HttpContext httpContext,
	PlatformDbContext dbContext,
	ApiTokenService tokenService,
	DownloadService downloadService,
	CancellationToken cancellationToken)
{
	var account = await GetCurrentAccountAsync(httpContext, dbContext, tokenService, cancellationToken);
	if (account is null)
	{
		return Results.Unauthorized();
	}

	var result = await downloadService.PreparePackageAsync(
		account.ID,
		assetId,
		httpContext.Connection.RemoteIpAddress?.ToString(),
		httpContext.Request.Headers.UserAgent.ToString(),
		cancellationToken);

	return result.Status switch
	{
		DownloadPackageStatus.Ready when result.Stream is not null => Results.File(result.Stream, "application/zip", result.FileName),
		DownloadPackageStatus.SubscriptionRequired => Results.Problem("当前账号没有该资源的有效订阅。", statusCode: StatusCodes.Status403Forbidden),
		DownloadPackageStatus.AccountDisabled => Results.Problem("账号已被禁用或冻结。", statusCode: StatusCodes.Status403Forbidden),
		DownloadPackageStatus.NoVersion => Results.Problem("该资源还没有可下载版本。", statusCode: StatusCodes.Status409Conflict),
		DownloadPackageStatus.FileMissing => Results.Problem("资源包文件不存在，请联系平台管理员补充包文件。", statusCode: StatusCodes.Status409Conflict),
		DownloadPackageStatus.PackageIntegrityFailed => Results.Problem("资源包校验失败，已暂停分发。", statusCode: StatusCodes.Status409Conflict),
		_ => Results.NotFound()
	};
}

static async Task<bool> HasActiveEntitlementAsync(PlatformDbContext dbContext, string accountId, string assetId, CancellationToken cancellationToken)
{
	var rows = await dbContext.Subscriptions
		.AsNoTracking()
		.Where(x => x.AccountId == accountId && x.AssetId == assetId && x.Status == SubscriptionStatus.Active)
		.ToListAsync(cancellationToken);

	var now = DateTimeOffset.UtcNow;
	return rows.Any(x => x.ExpiresAtUtc > now);
}

static async Task<bool> EnsureInstallEntitlementAsync(
	PlatformDbContext dbContext,
	SubscriptionService subscriptionService,
	string accountId,
	Netor.Cortana.Platform.Entitys.Tables.Assets.Asset asset,
	CancellationToken cancellationToken)
{
	var hasEntitlement = await HasActiveEntitlementAsync(dbContext, accountId, asset.ID, cancellationToken);
	if (hasEntitlement)
	{
		return true;
	}

	var freePlan = asset.PricingPlans.FirstOrDefault(x => x.IsActive && (x.PlanType == PricingPlanType.Free || x.Price <= 0));
	if (freePlan is null)
	{
		return false;
	}

	await subscriptionService.EnsureActiveSubscriptionAsync(accountId, asset.ID, freePlan, cancellationToken: cancellationToken);
	await dbContext.SaveChangesAsync(cancellationToken);
	return true;
}

static async Task<List<ApiSubscriptionItem>> GetSubscriptionItemsAsync(PlatformDbContext dbContext, string accountId, CancellationToken cancellationToken)
{
	var now = DateTimeOffset.UtcNow;
	var rows = await dbContext.Subscriptions
		.AsNoTracking()
		.Include(x => x.Asset)
		.Include(x => x.PricingPlan)
		.Where(x => x.AccountId == accountId)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.ToListAsync(cancellationToken);

	return rows
		.Select(x => new ApiSubscriptionItem(
			x.ID,
			x.AssetId,
			x.Asset?.Slug,
			x.Asset?.Type,
			x.Asset == null ? "未知资源" : x.Asset.Name,
			x.PricingPlanId,
			x.PricingPlan?.Name,
			x.PricingPlan?.PlanType,
			x.Status,
			x.StartedAtUtc,
			x.ExpiresAtUtc,
			x.ExpiresAtUtc <= now,
			(int)Math.Ceiling((x.ExpiresAtUtc - now).TotalDays)))
		.ToList();
}

static async Task<List<ApiOrderListItem>> GetOrderItemsAsync(PlatformDbContext dbContext, string accountId, CancellationToken cancellationToken)
	=> await dbContext.Orders
		.AsNoTracking()
		.Where(x => EF.Property<string>(x, "AccountID") == accountId)
		.OrderByDescending(x => EF.Property<long>(x, "TimeStamp"))
		.Select(x => new ApiOrderListItem(x.ID, x.No, x.Title, x.AssetId, x.PricingPlanId, x.Money, x.PayStatus, x.PayStatus == 2))
		.ToListAsync(cancellationToken);

static Netor.Cortana.Platform.Entitys.Tables.Assets.AssetVersion? GetCurrentVersion(
	string? currentVersionId,
	IEnumerable<Netor.Cortana.Platform.Entitys.Tables.Assets.AssetVersion> versions)
{
	var versionRows = versions.ToList();
	return string.IsNullOrWhiteSpace(currentVersionId)
		? versionRows.OrderByDescending(x => x.TimeStamp).FirstOrDefault()
		: versionRows.FirstOrDefault(x => x.ID == currentVersionId) ?? versionRows.OrderByDescending(x => x.TimeStamp).FirstOrDefault();
}

static ApiAssetVersion ToApiVersion(Netor.Cortana.Platform.Entitys.Tables.Assets.AssetVersion version)
{
	JsonElement? manifest = null;
	if (!string.IsNullOrWhiteSpace(version.ManifestJson))
	{
		manifest = JsonSerializer.Deserialize(version.ManifestJson, PlatformApiJsonContext.Default.JsonElement);
	}

	return new ApiAssetVersion(version.ID, version.VersionName, version.ReleaseNotes, version.PackageHash, version.PackageSize, manifest);
}

static ApiClientInstallManifest ToApiClientInstallManifest(
	Netor.Cortana.Platform.Entitys.Tables.Assets.Asset asset,
	Netor.Cortana.Platform.Entitys.Tables.Assets.AssetVersion version)
{
	JsonElement? manifest = null;
	if (!string.IsNullOrWhiteSpace(version.ManifestJson))
	{
		manifest = JsonSerializer.Deserialize(version.ManifestJson, PlatformApiJsonContext.Default.JsonElement);
	}

	return new ApiClientInstallManifest(
		asset.ID,
		asset.Slug,
		asset.Type,
		asset.Name,
		asset.DeveloperName,
		new ApiClientInstallVersion(
			version.ID,
			version.VersionName,
			version.ReleaseNotes,
			version.PackageHash,
			"SHA256",
			version.PackageSize),
		$"/api/v1/assets/{asset.ID}/download",
		manifest);
}

static ApiCreatorProfileResponse ToApiCreatorProfile(CreatorProfile profile)
	=> new(profile.ID, profile.AccountId, profile.DisplayName, profile.Bio, profile.Status, profile.ApprovedAtUtc);

static ApiPricingPlan ToApiPricingPlan(Netor.Cortana.Platform.Entitys.Tables.Assets.PricingPlan plan)
	=> new(plan.ID, plan.Name, plan.PlanType, plan.Price, plan.Currency, plan.DurationDays);

public sealed record ApiHealthResponse(string Status, string Application);

public sealed record ApiLoginRequest(string UserName, string Password);

public sealed record ApiLoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, ApiAccountResponse Account);

public sealed record ApiAccountResponse(string Id, long No, string LoginUserName, string NickName, string Email, string Phone);

public sealed record ApiCreateSubscriptionRequest(string PricingPlanId);

public sealed record ApiCreateOrderRequest(string AssetId, string PricingPlanId);

public sealed record ApiSubscribeResponse(bool IsFreeSubscription, string? AssetId, string? OrderId, string Status);

public sealed record ApiSubscriptionItem(
	string SubscriptionId,
	string AssetId,
	string? AssetSlug,
	AssetType? AssetType,
	string AssetName,
	string PricingPlanId,
	string? PricingPlanName,
	PricingPlanType? PricingPlanType,
	SubscriptionStatus Status,
	DateTimeOffset StartedAtUtc,
	DateTimeOffset ExpiresAtUtc,
	bool IsExpired,
	int RemainingDays);

public sealed record ApiCancelSubscriptionResponse(string SubscriptionId, bool Canceled);

public sealed record ApiDownloadRecord(
	string Id,
	string AssetId,
	string? AssetSlug,
	string AssetName,
	string AssetVersionId,
	string VersionName,
	string? IpAddress,
	long TimeStamp);

public sealed record ApiOrderListItem(
	string Id,
	string No,
	string Title,
	string? AssetId,
	string? PricingPlanId,
	decimal Amount,
	byte PayStatus,
	bool IsPaid);

public sealed record ApiOrderDetail(
	string Id,
	string No,
	string Title,
	string Content,
	string? AssetId,
	string? PricingPlanId,
	decimal Amount,
	int Numbers,
	byte PayStatus,
	byte PayMethod,
	bool IsPaid);

public sealed record ApiPayOrderResponse(string OrderId, string? OrderNo, decimal? Amount, bool IsPaid);

public sealed record ApiAssetListItem(
	string Id,
	string Slug,
	AssetType Type,
	string Name,
	string DeveloperName,
	string ShortDescription,
	string? IconUrl,
	string? CoverUrl,
	string? CategoryName,
	bool IsFeatured,
	int DownloadCount,
	bool HasFreePlan,
	decimal? MinPrice,
	string? CurrentVersionId,
	string? CurrentVersionName);

public sealed record ApiAssetPageResponse(
	IReadOnlyList<ApiAssetListItem> Items,
	int Page,
	int PageSize,
	int TotalCount,
	bool HasNextPage);

public sealed record ApiAssetDetail(
	string Id,
	string Slug,
	AssetType Type,
	string Name,
	string DeveloperName,
	string ShortDescription,
	string Description,
	string Tags,
	string? IconUrl,
	string? CoverUrl,
	string? CategoryName,
	int DownloadCount,
	ApiAssetVersion? CurrentVersion,
	IReadOnlyList<ApiPricingPlan> PricingPlans);

public sealed record ApiAssetVersion(
	string Id,
	string VersionName,
	string ReleaseNotes,
	string PackageHash,
	long PackageSize,
	JsonElement? Manifest = null);

public sealed record ApiPricingPlan(string Id, string Name, PricingPlanType PlanType, decimal Price, string Currency, int DurationDays);

public sealed record ApiEntitlement(
	string SubscriptionId,
	string AssetId,
	string AssetSlug,
	AssetType AssetType,
	string AssetName,
	string? PricingPlanName,
	DateTimeOffset StartedAtUtc,
	DateTimeOffset ExpiresAtUtc);

public sealed record ApiClientInstallManifest(
	string AssetId,
	string AssetSlug,
	AssetType AssetType,
	string AssetName,
	string DeveloperName,
	ApiClientInstallVersion Version,
	string DownloadUrl,
	JsonElement? Manifest);

public sealed record ApiClientInstallVersion(
	string VersionId,
	string VersionName,
	string ReleaseNotes,
	string PackageHash,
	string HashAlgorithm,
	long PackageSize);

public sealed record ApiClientSyncResponse(ApiAccountResponse Account, IReadOnlyList<ApiClientInstallManifest> Assets);

public sealed record ApiClientUpdateCheckRequest(IReadOnlyList<ApiClientInstalledAsset> InstalledAssets);

public sealed record ApiClientInstalledAsset(
	string? AssetId,
	string? AssetSlug,
	string? VersionId,
	string? VersionName,
	string? PackageHash);

public sealed record ApiClientUpdateCheckResponse(IReadOnlyList<ApiClientUpdateItem> Updates);

public sealed record ApiClientUpdateItem(
	string? InstalledAssetId,
	string? InstalledAssetSlug,
	string? InstalledVersionId,
	string? InstalledVersionName,
	ApiClientInstallManifest Current);

public sealed record ApiCreatorApplyRequest(string DisplayName, string? Bio);

public sealed record ApiCreatorProfileResponse(
	string Id,
	string AccountId,
	string DisplayName,
	string Bio,
	CreatorStatus Status,
	DateTimeOffset? ApprovedAtUtc);

public sealed record ApiCreatorPackageUploadResponse(
	string FilePath,
	string PackageHash,
	long PackageSize,
	string HashAlgorithm,
	string StorageProvider,
	string OriginalFileName);

public sealed record ApiCreatorAssetSubmitRequest(
	AssetType Type,
	string? CategoryId,
	string Name,
	string Slug,
	string? ShortDescription,
	string? Description,
	string? Tags,
	string? VersionName,
	string? ReleaseNotes,
	string? ManifestJson,
	string? PackageHash,
	string? StorageProvider,
	long PackageSize,
	string FilePath,
	decimal Price,
	string? Currency,
	int DurationDays);

public sealed record ApiCreatorAssetSubmitResponse(string AssetId, string AssetVersionId, string ReviewId);

public sealed record ApiCreatorAssetItem(
	string AssetId,
	string AssetSlug,
	AssetType AssetType,
	string AssetName,
	AssetStatus Status,
	string? VersionName,
	AssetReviewStatus? ReviewStatus,
	string? ReviewNotes,
	DateTimeOffset? PublishedAtUtc);

public sealed record ApiCreatorSettlementItem(
	string SettlementId,
	string AssetId,
	string AssetName,
	string OrderId,
	decimal GrossAmount,
	decimal PlatformFeeAmount,
	decimal NetAmount,
	string Currency,
	SettlementStatus Status,
	string Notes,
	DateTimeOffset? SettledAtUtc);

public sealed record ApiSkillManifest(string AssetId, string AssetSlug, string AssetName, ApiAssetVersion Version, JsonElement? Manifest);

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(IDictionary<string, string[]>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ApiHealthResponse))]
[JsonSerializable(typeof(MarketAssetListItem))]
[JsonSerializable(typeof(List<MarketAssetListItem>))]
[JsonSerializable(typeof(IReadOnlyList<MarketAssetListItem>))]
[JsonSerializable(typeof(ApiLoginRequest))]
[JsonSerializable(typeof(ApiLoginResponse))]
[JsonSerializable(typeof(ApiAccountResponse))]
[JsonSerializable(typeof(ApiCreateSubscriptionRequest))]
[JsonSerializable(typeof(ApiCreateOrderRequest))]
[JsonSerializable(typeof(ApiSubscribeResponse))]
[JsonSerializable(typeof(ApiSubscriptionItem))]
[JsonSerializable(typeof(List<ApiSubscriptionItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiSubscriptionItem>))]
[JsonSerializable(typeof(ApiCancelSubscriptionResponse))]
[JsonSerializable(typeof(ApiDownloadRecord))]
[JsonSerializable(typeof(List<ApiDownloadRecord>))]
[JsonSerializable(typeof(IReadOnlyList<ApiDownloadRecord>))]
[JsonSerializable(typeof(ApiOrderListItem))]
[JsonSerializable(typeof(List<ApiOrderListItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiOrderListItem>))]
[JsonSerializable(typeof(ApiOrderDetail))]
[JsonSerializable(typeof(ApiPayOrderResponse))]
[JsonSerializable(typeof(ApiAssetListItem))]
[JsonSerializable(typeof(List<ApiAssetListItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiAssetListItem>))]
[JsonSerializable(typeof(ApiAssetPageResponse))]
[JsonSerializable(typeof(ApiAssetDetail))]
[JsonSerializable(typeof(ApiAssetVersion))]
[JsonSerializable(typeof(ApiPricingPlan))]
[JsonSerializable(typeof(List<ApiPricingPlan>))]
[JsonSerializable(typeof(IReadOnlyList<ApiPricingPlan>))]
[JsonSerializable(typeof(ApiEntitlement))]
[JsonSerializable(typeof(List<ApiEntitlement>))]
[JsonSerializable(typeof(IReadOnlyList<ApiEntitlement>))]
[JsonSerializable(typeof(ApiClientInstallManifest))]
[JsonSerializable(typeof(ApiClientInstallVersion))]
[JsonSerializable(typeof(ApiClientSyncResponse))]
[JsonSerializable(typeof(ApiClientUpdateCheckRequest))]
[JsonSerializable(typeof(ApiClientInstalledAsset))]
[JsonSerializable(typeof(List<ApiClientInstalledAsset>))]
[JsonSerializable(typeof(IReadOnlyList<ApiClientInstalledAsset>))]
[JsonSerializable(typeof(ApiClientUpdateCheckResponse))]
[JsonSerializable(typeof(ApiClientUpdateItem))]
[JsonSerializable(typeof(List<ApiClientUpdateItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiClientUpdateItem>))]
[JsonSerializable(typeof(ApiCreatorApplyRequest))]
[JsonSerializable(typeof(ApiCreatorProfileResponse))]
[JsonSerializable(typeof(ApiCreatorPackageUploadResponse))]
[JsonSerializable(typeof(ApiCreatorAssetSubmitRequest))]
[JsonSerializable(typeof(ApiCreatorAssetSubmitResponse))]
[JsonSerializable(typeof(ApiCreatorAssetItem))]
[JsonSerializable(typeof(List<ApiCreatorAssetItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiCreatorAssetItem>))]
[JsonSerializable(typeof(ApiCreatorSettlementItem))]
[JsonSerializable(typeof(List<ApiCreatorSettlementItem>))]
[JsonSerializable(typeof(IReadOnlyList<ApiCreatorSettlementItem>))]
[JsonSerializable(typeof(ApiSkillManifest))]
internal sealed partial class PlatformApiJsonContext : JsonSerializerContext;
