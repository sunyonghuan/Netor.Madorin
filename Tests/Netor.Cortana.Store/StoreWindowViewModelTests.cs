using System.Text.Json;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.ViewModels;
using StoreAssetType = Netor.Cortana.Store.Models.AssetType;

namespace Netor.Cortana.Store.Tests;

public sealed class StoreWindowViewModelTests
{
    [Fact]
    public async Task GetInstallManifestAsync_WhenNotSignedIn_ReturnsNull()
    {
        var fixture = new ViewModelFixture();
        await fixture.ViewModel.RefreshSessionAsync();

        var manifest = await fixture.ViewModel.GetInstallManifestAsync(fixture.Asset);

        Assert.Null(manifest);
        Assert.Equal("需要登录平台账号后才能安装资源。", fixture.ViewModel.StatusText);
        Assert.Equal(0, fixture.MarketClient.InstallManifestRequests);
    }

    [Fact]
    public async Task GetInstallManifestAsync_WhenSignedIn_RequestsByAssetId()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        await fixture.ViewModel.RefreshSessionAsync();

        var manifest = await fixture.ViewModel.GetInstallManifestAsync(fixture.Asset);

        Assert.NotNull(manifest);
        Assert.Equal(1, fixture.MarketClient.InstallManifestRequests);
        Assert.Equal(fixture.Asset.Id, fixture.MarketClient.LastInstallManifestIdentifier);
    }

    [Fact]
    public async Task SyncAsync_LoadsOwnedAssetsAndUpdates()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.InstalledAssetStore.Assets =
        [
            fixture.CreateInstalledAsset("old-version", "0.9.0")
        ];
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.SyncAsync();

        Assert.True(fixture.ViewModel.IsSignedIn);
        Assert.Single(fixture.ViewModel.OwnedAssets);
        Assert.Single(fixture.ViewModel.Updates);
        Assert.Contains("1 个可更新", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task SyncAsync_WhenUpdateCheckFails_ReportsPartialSuccess()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateException = new InvalidOperationException("updates api unavailable");

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.SyncAsync();

        Assert.True(fixture.ViewModel.IsSignedIn);
        Assert.Single(fixture.ViewModel.OwnedAssets);
        Assert.Empty(fixture.ViewModel.Updates);
        Assert.Equal(1, fixture.MarketClient.UpdateCheckRequests);
        Assert.Contains("资源同步完成，当前账号拥有 1 个资源；检查更新失败：updates api unavailable", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_WhenSessionExists_SyncsOwnedAssetsAndUpdates()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.InstalledAssetStore.Assets =
        [
            fixture.CreateInstalledAsset("old-version", "0.9.0")
        ];
        fixture.MarketClient.Assets =
        [
            fixture.Asset
        ];
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.ViewModel.IsSignedIn);
        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Single(fixture.ViewModel.OwnedAssets);
        Assert.Single(fixture.ViewModel.Updates);
        Assert.Equal(1, fixture.MarketClient.SyncRequests);
        Assert.Equal(1, fixture.MarketClient.UpdateCheckRequests);
        Assert.Contains("同步完成", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task InstallAsync_InstallsManifestAndRefreshesUpdates()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.PackageInstallService.Result = PackageInstallResult.Success(fixture.CreateInstalledAsset("version-1", "1.0.0"));

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.InstallAsync(fixture.Manifest);

        Assert.Equal(fixture.Manifest, fixture.PackageInstallService.InstalledManifest);
        Assert.Equal("Demo Plugin 安装完成。", fixture.ViewModel.StatusText);
        Assert.Equal(1, fixture.MarketClient.UpdateCheckRequests);
    }

    [Fact]
    public async Task InstallAsync_WhenUpdateCheckFails_ReportsInstallSuccessAndUpdateFailure()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.PackageInstallService.Result = PackageInstallResult.Success(fixture.CreateInstalledAsset("version-1", "1.0.0"));
        fixture.MarketClient.UpdateException = new InvalidOperationException("updates api unavailable");

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.InstallAsync(fixture.Manifest);

        Assert.Equal(fixture.Manifest, fixture.PackageInstallService.InstalledManifest);
        Assert.Equal("Demo Plugin 安装完成；检查更新失败：updates api unavailable", fixture.ViewModel.StatusText);
        Assert.Equal(1, fixture.MarketClient.UpdateCheckRequests);
    }

    [Fact]
    public async Task UninstallAsync_UninstallsAssetAndRefreshesInstalledRows()
    {
        var fixture = new ViewModelFixture();
        var record = fixture.CreateInstalledAsset("version-1", "1.0.0");
        fixture.InstalledAssetStore.Assets = [record];
        fixture.PackageInstallService.UninstallCallback = asset => fixture.InstalledAssetStore.RemoveAsync(asset.AssetSlug);

        await fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.UninstallAsync(record);

        Assert.Equal(record, fixture.PackageInstallService.UninstalledAsset);
        Assert.Empty(fixture.ViewModel.InstalledAssetRows);
        Assert.Equal("Demo Plugin 卸载完成。", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task SearchText_WhenSet_FiltersMarketRows()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.Asset,
            fixture.CreateAsset("asset-2", "skill-search", StoreAssetType.Skill, "Skill Search", "FlowWorks", "流程编排技能")
        ];

        await fixture.ViewModel.RefreshAssetsAsync();
        fixture.ViewModel.SearchText = "FlowWorks";

        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Equal("Skill Search", fixture.ViewModel.MarketAssetRows[0].Name);
    }

    [Fact]
    public async Task SetMarketTypeFilter_WhenTypeSelected_FiltersMarketRows()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.Asset,
            fixture.CreateAsset("asset-2", "agent-review", StoreAssetType.Agent, "Review Agent", "Netor", "审核智能体")
        ];

        await fixture.ViewModel.RefreshAssetsAsync();
        fixture.ViewModel.SetMarketTypeFilter(StoreAssetType.Agent);

        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Equal("Review Agent", fixture.ViewModel.MarketAssetRows[0].Name);
        Assert.True(fixture.ViewModel.IsAgentMarketFilterActive);
        Assert.False(fixture.ViewModel.IsAllMarketFilterActive);
    }

    [Fact]
    public async Task SelectedCategoryFilter_WhenCategorySelected_FiltersMarketRows()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.CreateAsset("asset-1", "ssh", StoreAssetType.Plugin, "SSH Plugin", "Netor", "SSH 工具", categoryName: "运维"),
            fixture.CreateAsset("asset-2", "report", StoreAssetType.Skill, "Report Skill", "Netor", "报告技能", categoryName: "办公")
        ];

        await fixture.ViewModel.RefreshAssetsAsync();
        fixture.ViewModel.SelectedCategoryFilter = "办公";

        Assert.Equal(["全部分类", "办公", "运维"], fixture.ViewModel.CategoryFilterOptions);
        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Equal("Report Skill", fixture.ViewModel.MarketAssetRows[0].Name);
    }

    [Fact]
    public async Task ShowPopularAssets_SortsFeaturedAndDownloadCountFirst()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.CreateAsset("asset-1", "plain", StoreAssetType.Plugin, "Plain", "Netor", "普通资源", isFeatured: false, downloadCount: 100),
            fixture.CreateAsset("asset-2", "featured", StoreAssetType.Skill, "Featured", "Netor", "推荐资源", isFeatured: true, downloadCount: 1),
            fixture.CreateAsset("asset-3", "popular", StoreAssetType.Agent, "Popular", "Netor", "热门资源", isFeatured: false, downloadCount: 200)
        ];

        await fixture.ViewModel.RefreshAssetsAsync();
        fixture.ViewModel.ShowPopularAssets();

        Assert.True(fixture.ViewModel.IsPopularSortActive);
        Assert.Equal(["Featured", "Popular", "Plain"], fixture.ViewModel.MarketAssetRows.Select(x => x.Name));
    }

    [Fact]
    public async Task ShowAllAssets_ClearsSearchFilterAndPopularSort()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.Asset,
            fixture.CreateAsset("asset-2", "skill-search", StoreAssetType.Skill, "Skill Search", "FlowWorks", "流程编排技能")
        ];

        await fixture.ViewModel.RefreshAssetsAsync();
        fixture.ViewModel.SearchText = "FlowWorks";
        fixture.ViewModel.SetMarketTypeFilter(StoreAssetType.Skill);
        fixture.ViewModel.SelectedCategoryFilter = "技能";
        fixture.ViewModel.ShowPopularAssets();

        fixture.ViewModel.ShowAllAssets();

        Assert.Equal(string.Empty, fixture.ViewModel.SearchText);
        Assert.Equal("全部分类", fixture.ViewModel.SelectedCategoryFilter);
        Assert.True(fixture.ViewModel.IsAllMarketFilterActive);
        Assert.False(fixture.ViewModel.IsPopularSortActive);
        Assert.Equal(2, fixture.ViewModel.MarketAssetRows.Count);
    }

    [Fact]
    public async Task RefreshAssetsAsync_WhenCalledAgain_ReplacesMarketRows()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.AssetPages = new Queue<StoreAssetPage>(
        [
            new StoreAssetPage([fixture.Asset], 1, 20, 1, false)
        ]);

        await fixture.ViewModel.RefreshAssetsAsync();

        fixture.MarketClient.AssetPages = new Queue<StoreAssetPage>(
        [
            new StoreAssetPage([fixture.CreateAsset("asset-2", "skill-search", StoreAssetType.Skill, "Skill Search", "FlowWorks", "流程编排技能")], 1, 20, 1, false)
        ]);

        await fixture.ViewModel.RefreshAssetsAsync();

        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Equal("Skill Search", fixture.ViewModel.MarketAssetRows[0].Name);
        Assert.Contains("已加载全部 1 个资源", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task LoadMoreAssetsAsync_AppendsNextPageOnlyOnce()
    {
        var fixture = new ViewModelFixture();
        var secondAsset = fixture.CreateAsset("asset-2", "skill-search", StoreAssetType.Skill, "Skill Search", "FlowWorks", "流程编排技能");
        fixture.MarketClient.AssetPages = new Queue<StoreAssetPage>(
        [
            new StoreAssetPage([fixture.Asset], 1, 20, 2, true),
            new StoreAssetPage([secondAsset], 2, 20, 2, false)
        ]);

        await fixture.ViewModel.RefreshAssetsAsync();
        await fixture.ViewModel.LoadMoreAssetsAsync();
        await fixture.ViewModel.LoadMoreAssetsAsync();

        Assert.Equal(2, fixture.ViewModel.MarketAssetRows.Count);
        Assert.Equal(["Demo Plugin", "Skill Search"], fixture.ViewModel.MarketAssetRows.Select(x => x.Name));
        Assert.Equal(2, fixture.MarketClient.AssetRequests.Count);
        Assert.Equal((1, 20), fixture.MarketClient.AssetRequests[0]);
        Assert.Equal((2, 20), fixture.MarketClient.AssetRequests[1]);
        Assert.False(fixture.ViewModel.HasMoreMarketAssets);
    }

    [Fact]
    public async Task RefreshAssetsAsync_WhenLoadFails_ClearsMarketRows()
    {
        var fixture = new ViewModelFixture();
        fixture.MarketClient.Assets =
        [
            fixture.Asset
        ];

        await fixture.ViewModel.RefreshAssetsAsync();

        fixture.MarketClient.AssetException = new InvalidOperationException("assets api unavailable");
        await fixture.ViewModel.RefreshAssetsAsync();

        Assert.Empty(fixture.ViewModel.Assets);
        Assert.Empty(fixture.ViewModel.MarketAssetRows);
        Assert.Equal("资源市场加载失败：assets api unavailable", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task RefreshStoreAsync_WhenSignedIn_RefreshesMarketOwnedAndUpdates()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.InstalledAssetStore.Assets =
        [
            fixture.CreateInstalledAsset("old-version", "0.9.0")
        ];
        fixture.MarketClient.Assets =
        [
            fixture.CreateAsset("asset-2", "skill-search", StoreAssetType.Skill, "Skill Search", "FlowWorks", "流程编排技能")
        ];
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshStoreAsync();

        Assert.Single(fixture.ViewModel.MarketAssetRows);
        Assert.Single(fixture.ViewModel.OwnedAssets);
        Assert.Single(fixture.ViewModel.Updates);
        Assert.Equal(1, fixture.MarketClient.SyncRequests);
        Assert.Contains("同步完成", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task RefreshStoreAsync_WhenMarketFailsButSyncSucceeds_ReportsBothResults()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.MarketClient.AssetException = new InvalidOperationException("assets api unavailable");
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);

        await fixture.ViewModel.RefreshStoreAsync();

        Assert.Empty(fixture.ViewModel.Assets);
        Assert.Empty(fixture.ViewModel.MarketAssetRows);
        Assert.Single(fixture.ViewModel.OwnedAssets);
        Assert.Equal(1, fixture.MarketClient.SyncRequests);
        Assert.Contains("资源市场加载失败：assets api unavailable", fixture.ViewModel.StatusText);
        Assert.Contains("同步完成，当前账号拥有 1 个资源", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task RefreshUpdatesAsync_WhenLoadFails_ClearsUpdateRows()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.InstalledAssetStore.Assets =
        [
            fixture.CreateInstalledAsset("old-version", "0.9.0")
        ];
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.RefreshUpdatesAsync();

        fixture.MarketClient.UpdateException = new InvalidOperationException("updates api unavailable");
        await fixture.ViewModel.RefreshUpdatesAsync();

        Assert.Empty(fixture.ViewModel.Updates);
        Assert.Empty(fixture.ViewModel.UpdateRows);
        Assert.Equal("检查更新失败：updates api unavailable", fixture.ViewModel.StatusText);
    }

    [Fact]
    public async Task EnsureMarketAssetDetailAsync_SolutionShowsPackagedResourceSummary()
    {
        var fixture = new ViewModelFixture();
        var solutionAsset = fixture.CreateAsset("asset-2", "ops-solution", StoreAssetType.Solution, "Ops Solution", "Netor", "运维方案");
        fixture.MarketClient.DetailResponse = new StoreAssetDetail(
            solutionAsset.Id,
            solutionAsset.Slug,
            StoreAssetType.Solution,
            solutionAsset.Name,
            solutionAsset.DeveloperName,
            solutionAsset.ShortDescription,
            "用于运维的一套解决方案。",
            "运维",
            null,
            null,
            "方案",
            0,
            new StoreAssetVersion(
                "version-1",
                "1.0.0",
                "首个版本",
                new string('a', 64),
                2048,
                CreateSolutionManifestElement(includePlugin: true, includeSkill: true, includeAgent: false)),
            []);
        var row = StoreMarketAssetRow.FromAsset(solutionAsset, [], isSignedIn: true);

        await fixture.ViewModel.EnsureMarketAssetDetailAsync(row);

        Assert.Contains("方案内容", row.DetailText);
        Assert.Contains("插件：solution-plugin (1.0.0)", row.DetailText);
        Assert.Contains("技能：solution-skill (1.0.0)", row.DetailText);
        Assert.DoesNotContain("智能体：", row.DetailText);
    }

    [Fact]
    public async Task RefreshUpdatesAsync_DistinguishesSolutionChildResourceUpdates()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.InstalledAssetStore.Assets =
        [
            fixture.CreateInstalledAsset("old-version", "0.9.0", StoreAssetType.Solution, "ops-solution")
        ];
        var solutionManifest = new StoreInstallManifest(
            "asset-2",
            "ops-solution",
            StoreAssetType.Solution,
            "Ops Solution",
            "Netor",
            new StoreInstallVersion("version-2", "1.1.0", "升级方案", new string('b', 64), "SHA256", 2048),
            "https://platform.local/packages/ops-solution.zip",
            CreateSolutionManifestElement(includePlugin: true, includeSkill: false, includeAgent: true));
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-2", "ops-solution", "old-version", "0.9.0", solutionManifest),
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.RefreshUpdatesAsync();

        Assert.Equal(2, fixture.ViewModel.UpdateRows.Count);
        Assert.Equal("方案更新", fixture.ViewModel.UpdateRows[0].StateText);
        Assert.Contains("解决方案更新会同步包内子资源", fixture.ViewModel.UpdateRows[0].Description);
        Assert.Contains("插件：solution-plugin (1.0.0)", fixture.ViewModel.UpdateRows[0].Description);
        Assert.Contains("智能体：solution-agent (1.0.0)", fixture.ViewModel.UpdateRows[0].Description);
        Assert.Equal("单资源更新", fixture.ViewModel.UpdateRows[1].StateText);
    }

    [Fact]
    public async Task MarkSessionExpiredAsync_ClearsAccountScopedLists()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.SyncAsync();
        await fixture.ViewModel.MarkSessionExpiredAsync();

        Assert.False(fixture.ViewModel.IsSignedIn);
        Assert.Empty(fixture.ViewModel.OwnedAssets);
        Assert.Empty(fixture.ViewModel.Updates);
    }

    [Fact]
    public async Task RefreshSessionAsync_WhenBaseUrlChanged_ClearsAccountScopedLists()
    {
        var fixture = new ViewModelFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        fixture.MarketClient.SyncResponse = new ApiClientSyncResponse(fixture.Account, [fixture.Manifest]);
        fixture.MarketClient.UpdateResponse = new ApiClientUpdateCheckResponse(
        [
            new ApiClientUpdateItem("asset-1", "demo-plugin", "old-version", "0.9.0", fixture.Manifest)
        ]);

        await fixture.ViewModel.RefreshSessionAsync();
        await fixture.ViewModel.SyncAsync();

        fixture.BaseUrlProvider.BaseUrl = "https://new-platform.local";
        await fixture.ViewModel.RefreshSessionAsync();

        Assert.False(fixture.ViewModel.IsSignedIn);
        Assert.Empty(fixture.ViewModel.OwnedAssets);
        Assert.Empty(fixture.ViewModel.Updates);
        Assert.Null(fixture.AccountStore.Session);
        Assert.Equal("未登录", fixture.ViewModel.AccountText);
        Assert.Equal("运营平台地址已切换，请重新登录。", fixture.ViewModel.StatusText);
    }

    private static JsonElement CreateSolutionManifestElement(bool includePlugin, bool includeSkill, bool includeAgent)
    {
        var plugins = includePlugin
            ? """
              "plugins": [
                { "slug": "solution-plugin", "version": "1.0.0", "path": "plugins/solution-plugin" }
              ]
            """
            : """
              "plugins": []
            """;
        var skills = includeSkill
            ? """
              "skills": [
                { "slug": "solution-skill", "version": "1.0.0", "path": "skills/solution-skill" }
              ]
            """
            : """
              "skills": []
            """;
        var agents = includeAgent
            ? """
              "agents": [
                { "slug": "solution-agent", "version": "1.0.0", "path": "agents/solution-agent" }
              ]
            """
            : """
              "agents": []
            """;

        using var document = JsonDocument.Parse($$"""
        {
          "id": "demo.solution",
          "slug": "ops-solution",
          "name": "Ops Solution",
          "version": "1.0.0",
          "assets": {
            {{plugins}},
            {{skills}},
            {{agents}}
          }
        }
        """);
        return document.RootElement.Clone();
    }

    private sealed class ViewModelFixture
    {
        public ViewModelFixture()
        {
            ViewModel = new StoreWindowViewModel(
                AccountStore,
                MarketClient,
                InstalledAssetStore,
                BaseUrlProvider,
                PackageInstallService);
        }

        public ApiAccountResponse Account { get; } = new("account-1", 1, "tester", "测试用户", "tester@example.com", "13800000000");

        public StoreAssetItem Asset { get; } = new(
            "asset-1",
            "demo-plugin",
            StoreAssetType.Plugin,
            "Demo Plugin",
            "Netor",
            "Demo",
            null,
            null,
            "插件",
            false,
            0,
            true,
            null,
            "version-1",
            "1.0.0");

        public StoreInstallManifest Manifest { get; } = new(
            "asset-1",
            "demo-plugin",
            StoreAssetType.Plugin,
            "Demo Plugin",
            "Netor",
            new StoreInstallVersion("version-1", "1.0.0", "release", new string('a', 64), "SHA256", 1024),
            "https://platform.local/packages/demo.zip",
            null);

        public TestAccountStore AccountStore { get; } = new();

        public TestMarketClient MarketClient { get; } = new();

        public TestInstalledAssetStore InstalledAssetStore { get; } = new();

        public TestPackageInstallService PackageInstallService { get; } = new();

        public TestBaseUrlProvider BaseUrlProvider { get; } = new();

        public StoreWindowViewModel ViewModel { get; }

        public PlatformAccountSession CreateSession()
            => new("https://platform.local", "token", DateTimeOffset.UtcNow.AddHours(1), Account);

        public StoreAssetItem CreateAsset(
            string id,
            string slug,
            StoreAssetType assetType,
            string name,
            string developerName,
            string description,
            bool isFeatured = false,
            int downloadCount = 0,
            string? categoryName = null)
            => new(
                id,
                slug,
                assetType,
                name,
                developerName,
                description,
                null,
                null,
                categoryName ?? ToCategoryName(assetType),
                isFeatured,
                downloadCount,
                true,
                null,
                "version-1",
                "1.0.0");

        private static string ToCategoryName(StoreAssetType assetType)
            => assetType switch
            {
                StoreAssetType.Plugin => "插件",
                StoreAssetType.Skill => "技能",
                StoreAssetType.Agent => "智能体",
                StoreAssetType.Solution => "方案",
                _ => "资源"
            };

        public InstalledAssetRecord CreateInstalledAsset(
            string versionId,
            string versionName,
            StoreAssetType assetType = StoreAssetType.Plugin,
            string assetSlug = "demo-plugin")
            => new(
                assetType == StoreAssetType.Solution ? "asset-2" : "asset-1",
                assetSlug,
                assetType,
                assetType == StoreAssetType.Solution ? "Ops Solution" : "Demo Plugin",
                versionId,
                versionName,
                new string('a', 64),
                1024,
                @$"C:\{assetType.ToString().ToLowerInvariant()}s\{assetSlug}",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
    }

    private sealed class TestBaseUrlProvider : IPlatformBaseUrlProvider
    {
        public string BaseUrl { get; set; } = "https://platform.local";

        public string GetBaseUrl() => BaseUrl;
    }

    private sealed class TestAccountStore : IPlatformAccountStore
    {
        public PlatformAccountSession? Session { get; set; }

        public Task<PlatformAccountSession?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Session);

        public Task SaveAsync(PlatformAccountSession session, CancellationToken cancellationToken = default)
        {
            Session = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Session = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TestMarketClient : IPlatformMarketClient
    {
        public int InstallManifestRequests { get; private set; }

        public string? LastInstallManifestIdentifier { get; private set; }

        public int SyncRequests { get; private set; }

        public int UpdateCheckRequests { get; private set; }

        public List<(int Page, int PageSize)> AssetRequests { get; } = [];

        public ApiClientSyncResponse SyncResponse { get; set; } = new(new ApiAccountResponse("account-1", 1, "tester", "测试用户", "", ""), []);

        public ApiClientUpdateCheckResponse UpdateResponse { get; set; } = new([]);

        public Exception? AssetException { get; set; }

        public Exception? UpdateException { get; set; }

        public IReadOnlyList<StoreAssetItem> Assets { get; set; } = [];

        public StoreAssetDetail? DetailResponse { get; set; }

        public Queue<StoreAssetPage> AssetPages { get; set; } = new();

        public Task<ApiLoginResponse> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ApiAccountResponse> GetMeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SyncResponse.Account);

        public Task<StoreAssetPage> GetAssetsAsync(int page = 1, int pageSize = 20, StoreAssetType? type = null, CancellationToken cancellationToken = default)
        {
            AssetRequests.Add((page, pageSize));
            if (AssetException is not null)
            {
                throw AssetException;
            }

            if (AssetPages.Count > 0)
            {
                return Task.FromResult(AssetPages.Dequeue());
            }

            return Task.FromResult(new StoreAssetPage(Assets, page, pageSize, Assets.Count, false));
        }

        public Task<StoreAssetDetail> GetAssetDetailAsync(string assetIdOrSlug, CancellationToken cancellationToken = default)
            => Task.FromResult(DetailResponse ?? new StoreAssetDetail(
                "asset-1",
                "demo-plugin",
                StoreAssetType.Plugin,
                "Demo Plugin",
                "Netor",
                "Demo",
                "用于演示的插件详情。",
                "搜索,自动化",
                null,
                null,
                "插件",
                0,
                new StoreAssetVersion("version-1", "1.0.0", "首个版本", new string('a', 64), 1024),
                []));

        public Task<StoreInstallManifest> GetInstallManifestAsync(string assetIdOrSlug, CancellationToken cancellationToken = default)
        {
            InstallManifestRequests++;
            LastInstallManifestIdentifier = assetIdOrSlug;
            return Task.FromResult(SyncResponse.Assets.First());
        }

        public Task<ApiClientSyncResponse> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncRequests++;
            return Task.FromResult(SyncResponse);
        }

        public Task<ApiClientUpdateCheckResponse> CheckUpdatesAsync(IReadOnlyList<ApiClientInstalledAsset> installedAssets, CancellationToken cancellationToken = default)
        {
            UpdateCheckRequests++;
            if (UpdateException is not null)
            {
                throw UpdateException;
            }

            return Task.FromResult(UpdateResponse);
        }
    }

    private sealed class TestInstalledAssetStore : IInstalledAssetStore
    {
        public IReadOnlyList<InstalledAssetRecord> Assets { get; set; } = [];

        public Task<IReadOnlyList<InstalledAssetRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Assets);

        public Task SaveAsync(IReadOnlyList<InstalledAssetRecord> assets, CancellationToken cancellationToken = default)
        {
            Assets = assets;
            return Task.CompletedTask;
        }

        public Task UpsertAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default)
        {
            Assets = [asset];
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string assetSlug, CancellationToken cancellationToken = default)
        {
            Assets = [.. Assets.Where(x => !string.Equals(x.AssetSlug, assetSlug, StringComparison.OrdinalIgnoreCase))];
            return Task.CompletedTask;
        }
    }

    private sealed class TestPackageInstallService : IPackageInstallService
    {
        public StoreInstallManifest? InstalledManifest { get; private set; }

        public InstalledAssetRecord? UninstalledAsset { get; private set; }

        public PackageInstallResult Result { get; set; } = PackageInstallResult.Failed("not configured");

        public Func<InstalledAssetRecord, Task>? UninstallCallback { get; set; }

        public Task CleanupStaleStagingAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<PackageInstallResult> InstallAsync(StoreInstallManifest manifest, CancellationToken cancellationToken = default)
        {
            InstalledManifest = manifest;
            return Task.FromResult(Result);
        }

        public async Task<PackageInstallResult> UninstallAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default)
        {
            UninstalledAsset = asset;
            if (UninstallCallback is not null)
            {
                await UninstallCallback(asset);
            }

            return PackageInstallResult.Uninstalled(asset);
        }
    }
}
