using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Extensions.EncryptExtensions;

namespace Netor.Cortana.Platform.Tests;

public sealed class PlatformApiIntegrationTests
{
    [Fact]
    public async Task ClientFlow_WithFreeSkill_CreatesEntitlementAndDownloadsPackage()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Skill, price: 0, filePath: "skills/free.zip");
        await fixture.WritePackageAsync("skills/free.zip", [1, 2, 3, 4]);
        await fixture.LoginAsync(client);

        var manifestResponse = await client.GetAsync($"/api/v1/skills/{asset.AssetId}/manifest");
        var entitlementsResponse = await client.GetAsync("/api/v1/entitlements");
        var downloadResponse = await client.GetAsync($"/api/v1/skills/{asset.AssetId}/download");

        manifestResponse.EnsureSuccessStatusCode();
        entitlementsResponse.EnsureSuccessStatusCode();
        downloadResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], await downloadResponse.Content.ReadAsByteArrayAsync());
        await using var dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Subscriptions.Where(x => x.AssetId == asset.AssetId).ToListAsync());
        Assert.Single(await dbContext.DownloadRecords.Where(x => x.AssetId == asset.AssetId).ToListAsync());
    }

    [Fact]
    public async Task AssetDownload_WithFreePlugin_ReturnsPackage()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Plugin, price: 0, filePath: "plugins/free.zip");
        await fixture.WritePackageAsync("plugins/free.zip", [9, 8, 7]);
        await fixture.LoginAsync(client);

        var response = await client.GetAsync($"/api/v1/assets/{asset.AssetId}/download");

        response.EnsureSuccessStatusCode();
        Assert.Equal([9, 8, 7], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AssetDownload_WithSlug_ReturnsPackage()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Plugin, price: 0, filePath: "plugins/free.zip");
        await fixture.WritePackageAsync("plugins/free.zip", [9, 8, 7]);
        await fixture.LoginAsync(client);

        var response = await client.GetAsync($"/api/v1/assets/{asset.AssetSlug}/download");

        response.EnsureSuccessStatusCode();
        Assert.Equal([9, 8, 7], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AssetDownload_WhenPackageMissing_ReturnsConflictProblemDetails()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Plugin, price: 0, filePath: "plugins/missing.zip");
        await fixture.LoginAsync(client);

        var response = await client.GetAsync($"/api/v1/assets/{asset.AssetId}/download");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("资源包文件不存在", body);
    }

    [Fact]
    public async Task ClientInstallSyncAndUpdate_WithFreePlugin_ReturnsInstallManifests()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Plugin, price: 0, filePath: "plugins/free.zip");
        await fixture.WritePackageAsync("plugins/free.zip", [1, 2, 3]);
        await fixture.LoginAsync(client);

        var installResponse = await client.GetAsync($"/api/v1/client/install/{asset.AssetId}");
        installResponse.EnsureSuccessStatusCode();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var installJson = await installResponse.Content.ReadAsStringAsync();
        var install = JsonSerializer.Deserialize<ApiClientInstallManifest>(installJson, jsonOptions);
        Assert.NotNull(install);
        Assert.Equal(asset.AssetId, install.AssetId);
        Assert.Equal("SHA256", install.Version.HashAlgorithm);
        Assert.Equal($"/api/v1/assets/{asset.AssetId}/download", install.DownloadUrl);
        var storeInstall = JsonSerializer.Deserialize<ClientCompatibleInstallManifest>(installJson, jsonOptions);
        Assert.NotNull(storeInstall);
        Assert.Equal(asset.AssetId, storeInstall.AssetId);
        Assert.Equal(install.DownloadUrl, storeInstall.DownloadUrl);

        var syncResponse = await client.GetAsync("/api/v1/client/sync");
        syncResponse.EnsureSuccessStatusCode();
        var syncJson = await syncResponse.Content.ReadAsStringAsync();
        var sync = JsonSerializer.Deserialize<ApiClientSyncResponse>(syncJson, jsonOptions);
        Assert.NotNull(sync);
        Assert.Contains(sync.Assets, x => x.AssetId == asset.AssetId);
        var storeSync = JsonSerializer.Deserialize<ClientCompatibleSyncResponse>(syncJson, jsonOptions);
        Assert.NotNull(storeSync);
        Assert.Contains(storeSync.Assets, x => x.AssetId == asset.AssetId);

        var newVersionId = await fixture.AddVersionAsync(asset.AssetId, "2.0.0", "plugins/free-v2.zip", setCurrent: true);
        await fixture.WritePackageAsync("plugins/free-v2.zip", [4, 5, 6]);
        var updateResponse = await client.PostAsJsonAsync("/api/v1/client/updates", new ApiClientUpdateCheckRequest(
        [
            new ApiClientInstalledAsset(asset.AssetId, null, install.Version.VersionId, install.Version.VersionName, install.Version.PackageHash)
        ]));
        updateResponse.EnsureSuccessStatusCode();
        var updateJson = await updateResponse.Content.ReadAsStringAsync();
        var update = JsonSerializer.Deserialize<ApiClientUpdateCheckResponse>(updateJson, jsonOptions);

        Assert.NotNull(update);
        var item = Assert.Single(update.Updates);
        Assert.Equal(newVersionId, item.Current.Version.VersionId);
        Assert.Equal("2.0.0", item.Current.Version.VersionName);
        var storeUpdate = JsonSerializer.Deserialize<ClientCompatibleUpdateCheckResponse>(updateJson, jsonOptions);
        Assert.NotNull(storeUpdate);
        var storeItem = Assert.Single(storeUpdate.Updates);
        Assert.Equal(newVersionId, storeItem.Current.Version.VersionId);
    }

    [Fact]
    public async Task ClientUpdates_WithSameVersionName_DoesNotReturnUpdate()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Plugin, price: 0, filePath: "plugins/free.zip");
        await fixture.WritePackageAsync("plugins/free.zip", [1, 2, 3]);
        await fixture.LoginAsync(client);

        var installResponse = await client.GetAsync($"/api/v1/client/install/{asset.AssetId}");
        installResponse.EnsureSuccessStatusCode();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var installJson = await installResponse.Content.ReadAsStringAsync();
        var install = JsonSerializer.Deserialize<ApiClientInstallManifest>(installJson, jsonOptions);

        Assert.NotNull(install);

        var updateResponse = await client.PostAsJsonAsync("/api/v1/client/updates", new ApiClientUpdateCheckRequest(
        [
            new ApiClientInstalledAsset(
                asset.AssetId,
                null,
                "local-version-id",
                install.Version.VersionName,
                "local-package-hash")
        ]));
        updateResponse.EnsureSuccessStatusCode();

        var update = await updateResponse.Content.ReadFromJsonAsync<ApiClientUpdateCheckResponse>(jsonOptions);

        Assert.NotNull(update);
        Assert.Empty(update.Updates);
    }

    [Fact]
    public async Task ClientFlow_WithPaidAsset_CreatesOrderPaymentAndEntitlement()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Agent, price: 19.9m, filePath: "agents/paid.zip");
        await fixture.LoginAsync(client);

        var createOrderResponse = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            assetId = asset.AssetId,
            pricingPlanId = asset.PricingPlanId
        });
        createOrderResponse.EnsureSuccessStatusCode();

        var createOrder = await createOrderResponse.Content.ReadFromJsonAsync<ApiSubscribeResponse>();
        Assert.NotNull(createOrder);
        Assert.False(createOrder.IsFreeSubscription);
        Assert.NotNull(createOrder.OrderId);

        var payResponse = await client.PostAsync($"/api/v1/orders/{createOrder.OrderId}/pay", null);
        var entitlementsResponse = await client.GetAsync("/api/v1/entitlements");

        payResponse.EnsureSuccessStatusCode();
        entitlementsResponse.EnsureSuccessStatusCode();
        var payment = await payResponse.Content.ReadFromJsonAsync<ApiPayOrderResponse>();
        var entitlements = await entitlementsResponse.Content.ReadFromJsonAsync<List<ApiEntitlement>>();

        Assert.NotNull(payment);
        Assert.True(payment.IsPaid);
        Assert.Contains(entitlements!, x => x.AssetId == asset.AssetId);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Orders.Where(x => x.AssetId == asset.AssetId).ToListAsync());
        Assert.Single(await dbContext.Transactions.ToListAsync());
        Assert.Single(await dbContext.Subscriptions.Where(x => x.AssetId == asset.AssetId).ToListAsync());
    }

    [Fact]
    public async Task GetMe_WithoutToken_ReturnsUnauthorized()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithDisabledAccount_ReturnsForbidden()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        await fixture.SetAccountStatusAsync(1);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userName = "api-tester",
            password = "123456"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SkillManifest_WithPaidSkillWithoutEntitlement_ReturnsForbidden()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        var asset = await fixture.SeedAssetAsync(AssetType.Skill, price: 19.9m, filePath: "skills/paid.zip");
        await fixture.LoginAsync(client);

        var response = await client.GetAsync($"/api/v1/skills/{asset.AssetId}/manifest");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Subscriptions.Where(x => x.AssetId == asset.AssetId).ToListAsync());
    }

    [Fact]
    public async Task CreatorFlow_WithPackageUpload_SubmitsDraftAssetForReview()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        await fixture.LoginAsync(client);

        var applyResponse = await client.PostAsJsonAsync("/api/v1/creator/apply", new
        {
            displayName = "API Creator",
            bio = "Uploads test packages"
        });
        applyResponse.EnsureSuccessStatusCode();
        await fixture.ApproveCreatorAsync("api-tester");

        using var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent(((int)AssetType.Plugin).ToString()), "type");
        uploadContent.Add(new StringContent("api-creator-plugin"), "slug");
        uploadContent.Add(new StringContent("1.0.0"), "versionName");
        uploadContent.Add(new ByteArrayContent([1, 2, 3]), "package", "api-creator-plugin.zip");
        var uploadResponse = await client.PostAsync("/api/v1/creator/packages", uploadContent);
        uploadResponse.EnsureSuccessStatusCode();
        var upload = await uploadResponse.Content.ReadFromJsonAsync<ApiCreatorPackageUploadResponse>();
        Assert.NotNull(upload);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", upload.PackageHash);
        Assert.Equal(3, upload.PackageSize);
        Assert.Equal("Local", upload.StorageProvider);

        var submitResponse = await client.PostAsJsonAsync("/api/v1/creator/assets", new ApiCreatorAssetSubmitRequest(
            AssetType.Plugin,
            null,
            "API Creator Plugin",
            "api-creator-plugin",
            "Uploaded through creator API",
            "Uploaded through creator API",
            "plugin",
            "1.0.0",
            "Initial upload",
            "{}",
            upload.PackageHash,
            upload.StorageProvider,
            upload.PackageSize,
            upload.FilePath,
            9.9m,
            "CNY",
            30));
        submitResponse.EnsureSuccessStatusCode();
        var submit = await submitResponse.Content.ReadFromJsonAsync<ApiCreatorAssetSubmitResponse>();

        Assert.NotNull(submit);
        await using var dbContext = fixture.CreateDbContext();
        var asset = await dbContext.Assets.SingleAsync(x => x.ID == submit.AssetId);
        var version = await dbContext.AssetVersions.SingleAsync(x => x.ID == submit.AssetVersionId);
        var review = await dbContext.AssetReviews.SingleAsync(x => x.ID == submit.ReviewId);
        Assert.Equal(AssetStatus.Draft, asset.Status);
        Assert.Equal("Local", version.StorageProvider);
        Assert.Equal(AssetReviewStatus.Pending, review.Status);
        Assert.True(fixture.PackageExists(upload.FilePath));
    }

    [Fact]
    public async Task CreatorPackageUpload_WithInvalidSlug_ReturnsBadRequest()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        await fixture.LoginAsync(client);
        await client.PostAsJsonAsync("/api/v1/creator/apply", new
        {
            displayName = "API Creator",
            bio = "Uploads test packages"
        });
        await fixture.ApproveCreatorAsync("api-tester");

        using var uploadContent = new MultipartFormDataContent();
        uploadContent.Add(new StringContent(((int)AssetType.Plugin).ToString()), "type");
        uploadContent.Add(new StringContent("bad slug"), "slug");
        uploadContent.Add(new StringContent("1.0.0"), "versionName");
        uploadContent.Add(new ByteArrayContent([1, 2, 3]), "package", "bad-slug.zip");
        var uploadResponse = await client.PostAsync("/api/v1/creator/packages", uploadContent);

        Assert.Equal(HttpStatusCode.BadRequest, uploadResponse.StatusCode);
    }

    [Fact]
    public async Task CreatorAssetSubmit_WithUnknownStorageProvider_ReturnsBadRequest()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        await fixture.LoginAsync(client);
        await client.PostAsJsonAsync("/api/v1/creator/apply", new
        {
            displayName = "API Creator",
            bio = "Uploads test packages"
        });
        await fixture.ApproveCreatorAsync("api-tester");

        var response = await client.PostAsJsonAsync("/api/v1/creator/assets", new ApiCreatorAssetSubmitRequest(
            AssetType.Plugin,
            null,
            "Unknown Provider Plugin",
            "unknown-provider-plugin",
            "Uploaded through creator API",
            "Uploaded through creator API",
            "plugin",
            "1.0.0",
            "Initial upload",
            "{}",
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            "MissingProvider",
            3,
            "packages/plugins/unknown-provider-plugin/1.0.0/unknown-provider-plugin.zip",
            9.9m,
            "CNY",
            30));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Assets.Where(x => x.Slug == "unknown-provider-plugin").ToListAsync());
        Assert.Empty(await dbContext.AssetReviews.ToListAsync());
    }

    [Fact]
    public async Task CreatorAssetSubmit_WithUnconfiguredS3StorageProvider_ReturnsBadRequest()
    {
        await using var fixture = await PlatformApiTestFixture.CreateAsync();
        using var client = await fixture.CreateStartedClientAsync();
        await fixture.LoginAsync(client);
        await client.PostAsJsonAsync("/api/v1/creator/apply", new
        {
            displayName = "API Creator",
            bio = "Uploads test packages"
        });
        await fixture.ApproveCreatorAsync("api-tester");

        var response = await client.PostAsJsonAsync("/api/v1/creator/assets", new ApiCreatorAssetSubmitRequest(
            AssetType.Plugin,
            null,
            "Unconfigured S3 Plugin",
            "unconfigured-s3-plugin",
            "Uploaded through creator API",
            "Uploaded through creator API",
            "plugin",
            "1.0.0",
            "Initial upload",
            "{}",
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            "S3",
            3,
            "packages/plugins/unconfigured-s3-plugin/1.0.0/unconfigured-s3-plugin.zip",
            9.9m,
            "CNY",
            30));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Assets.Where(x => x.Slug == "unconfigured-s3-plugin").ToListAsync());
        Assert.Empty(await dbContext.AssetReviews.ToListAsync());
    }
}

internal sealed record ClientCompatibleInstallManifest(
    string AssetId,
    string AssetSlug,
    AssetType AssetType,
    string AssetName,
    string DeveloperName,
    ClientCompatibleInstallVersion Version,
    string DownloadUrl,
    JsonElement? Manifest);

internal sealed record ClientCompatibleInstallVersion(
    string VersionId,
    string VersionName,
    string ReleaseNotes,
    string PackageHash,
    string HashAlgorithm,
    long PackageSize);

internal sealed record ClientCompatibleSyncResponse(
    ApiAccountResponse Account,
    IReadOnlyList<ClientCompatibleInstallManifest> Assets);

internal sealed record ClientCompatibleUpdateCheckResponse(
    IReadOnlyList<ClientCompatibleUpdateItem> Updates);

internal sealed record ClientCompatibleUpdateItem(
    string? InstalledAssetId,
    string? InstalledAssetSlug,
    string? InstalledVersionId,
    string? InstalledVersionName,
    ClientCompatibleInstallManifest Current);

internal sealed class PlatformApiTestFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<Program> _factory;

    private PlatformApiTestFixture(string root, WebApplicationFactory<Program> factory)
    {
        _root = root;
        _factory = factory;
    }

    private string DatabasePath => Path.Combine(_root, "platform-api-test.db");

    private string StorageRoot => Path.Combine(_root, "storage");

    public static async Task<PlatformApiTestFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortana-platform-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "storage", "packages"));

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Database:Provider"] = "Sqlite",
                        ["Database:ConnectionString"] = $"Data Source={Path.Combine(root, "platform-api-test.db")}",
                        ["FileStorage:RootPath"] = Path.Combine(root, "storage"),
                        ["ApiToken:SigningKey"] = "integration-test-signing-key"
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<PlatformDbContext>();
                    services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
                    services.RemoveAll<DbContextOptions<PlatformDbContext>>();
                    services.AddDbContext<PlatformDbContext>(options =>
                    {
                        options.ConfigureWarnings(warnings =>
                            warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
                        options.UseSqlite($"Data Source={Path.Combine(root, "platform-api-test.db")}");
                    });
                });
            });

        return new PlatformApiTestFixture(root, factory);
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task<HttpClient> CreateStartedClientAsync()
    {
        await InitializeDatabaseAsync();
        var client = CreateClient();
        var response = await client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();
        await SeedAccountAsync();
        return client;
    }

    public PlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .UseSqlite($"Data Source={DatabasePath}")
            .Options;

        return new PlatformDbContext(options);
    }

    public async Task LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userName = "api-tester",
            password = "123456"
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiLoginResponse>();
        Assert.NotNull(body);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
    }

    public async Task<SeededAsset> SeedAssetAsync(AssetType type, decimal price, string filePath)
    {
        await using var dbContext = CreateDbContext();
        var category = dbContext.Categories.Add(new Category
        {
            Name = $"{type} {Guid.NewGuid():N}",
            Slug = $"category-{Guid.NewGuid():N}"
        }).Entity;

        var asset = dbContext.Assets.Add(new Asset
        {
            Type = type,
            Category = category,
            Name = $"Asset {Guid.NewGuid():N}",
            Slug = $"asset-{Guid.NewGuid():N}",
            DeveloperName = "Netor",
            ShortDescription = "Integration test asset",
            Description = "Integration test asset",
            Status = AssetStatus.Published,
            PublishedAtUtc = DateTimeOffset.UtcNow,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = "1.0.0",
                    ReleaseNotes = "Test",
                    ManifestJson = JsonSerializer.Serialize(new { name = "test-skill" }),
                    PackageHash = string.Empty,
                    PackageSize = 4,
                    FilePath = filePath
                }
            ],
            PricingPlans =
            [
                new PricingPlan
                {
                    Name = price <= 0 ? "免费版" : "月度订阅",
                    PlanType = price <= 0 ? PricingPlanType.Free : PricingPlanType.Monthly,
                    Price = price,
                    DurationDays = price <= 0 ? 0 : 30,
                    IsActive = true
                }
            ]
        }).Entity;

        await dbContext.SaveChangesAsync();
        return new SeededAsset(asset.ID, asset.Slug, asset.PricingPlans.Single().ID);
    }

    public async Task WritePackageAsync(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(StorageRoot, "packages", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        await using var dbContext = CreateDbContext();
        var version = await dbContext.AssetVersions.FirstOrDefaultAsync(x => x.FilePath == relativePath);
        if (version is not null)
        {
            version.PackageHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            version.PackageSize = bytes.LongLength;
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task<string> AddVersionAsync(string assetId, string versionName, string filePath, bool setCurrent)
    {
        await using var dbContext = CreateDbContext();
        var asset = await dbContext.Assets.FirstAsync(x => x.ID == assetId);
        var version = dbContext.AssetVersions.Add(new AssetVersion
        {
            AssetId = assetId,
            VersionName = versionName,
            ReleaseNotes = "Integration test update",
            ManifestJson = JsonSerializer.Serialize(new { name = "test-update" }),
            PackageHash = string.Empty,
            PackageSize = 0,
            FilePath = filePath
        }).Entity;

        if (setCurrent)
        {
            asset.CurrentVersionId = version.ID;
        }

        await dbContext.SaveChangesAsync();
        return version.ID;
    }

    public async Task SetAccountStatusAsync(byte status)
    {
        await using var dbContext = CreateDbContext();
        var account = await dbContext.Accounts.FirstAsync(x => x.LoginUserName == "api-tester");
        account.Status = status;
        await dbContext.SaveChangesAsync();
    }

    public async Task ApproveCreatorAsync(string loginUserName)
    {
        await using var dbContext = CreateDbContext();
        var account = await dbContext.Accounts.FirstAsync(x => x.LoginUserName == loginUserName);
        var profile = await dbContext.CreatorProfiles.FirstAsync(x => x.AccountId == account.ID);
        profile.Status = CreatorStatus.Approved;
        profile.ApprovedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    public bool PackageExists(string relativePath)
    {
        var path = Path.Combine(StorageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path);
    }

    private async Task SeedAccountAsync()
    {
        await using var dbContext = CreateDbContext();
        var accountExists = await dbContext.Accounts.AnyAsync(x => x.LoginUserName == "api-tester");
        if (accountExists)
        {
            return;
        }

        dbContext.Accounts.Add(new Account
        {
            No = Random.Shared.NextInt64(900000100000, 900000999999),
            LoginUserName = "api-tester",
            Email = "api-tester@example.com",
            Phone = "13900139000",
            LoginPassword = "123456".MD5Encrypt(),
            SafePassword = "123456".MD5Encrypt()
        });

        await dbContext.SaveChangesAsync();
    }

    private async Task InitializeDatabaseAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        await dbContext.EnsureSeededAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            await DeleteDirectoryWithRetryAsync(_root);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }
}
