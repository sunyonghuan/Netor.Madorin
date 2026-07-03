using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Entitys;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.Services;
using Netor.EventHub;
using StoreAssetType = Netor.Cortana.Store.Models.AssetType;

namespace Netor.Cortana.Store.Tests;

public sealed class PackageInstallServiceTests
{
    [Fact]
    public async Task InstallAsync_PluginPackage_InstallsAndRecordsAsset()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreatePluginZip(includeDirectoryEntry: true);
        var manifest = fixture.CreateManifest(zip);

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, manifest.AssetSlug, "plugin.json")));
        Assert.Contains(fixture.PluginActivation.LoadedPaths, x => x.EndsWith(manifest.AssetSlug, StringComparison.OrdinalIgnoreCase));

        var installed = await fixture.AssetStore.LoadAsync();
        var record = Assert.Single(installed);
        Assert.Equal(manifest.AssetSlug, record.AssetSlug);
        Assert.Equal(manifest.Version.PackageHash, record.PackageHash);
        Assert.Equal(new Uri("https://platform.local/api/v1/assets/asset-1/download"), fixture.HttpClientFactory.LastRequestUri);
        Assert.Equal("Bearer", fixture.HttpClientFactory.LastAuthorizationScheme);
    }

    [Fact]
    public async Task InstallAsync_WhenPackageContainsSymbolicLink_DoesNotInstall()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreatePluginZipWithSymlink();
        var manifest = fixture.CreateManifest(zip);

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("符号链接", result.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, manifest.AssetSlug)));
    }

    [Fact]
    public async Task InstallAsync_WhenSha256Differs_DoesNotInstall()
    {
        using var fixture = new StoreInstallFixture();
        var zip = CreatePluginZip();
        var manifest = fixture.CreateManifest(zip) with
        {
            Version = fixture.CreateVersion(zip) with { PackageHash = new string('0', 64) }
        };

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("SHA256", result.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, manifest.AssetSlug)));
    }

    [Fact]
    public async Task InstallAsync_WhenManifestMissing_DoesNotInstall()
    {
        using var fixture = new StoreInstallFixture();
        var zip = CreateZip(("readme.txt", "hello"));
        var manifest = fixture.CreateManifest(zip);

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.False(result.Succeeded);
        Assert.Contains("plugin.json", result.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, manifest.AssetSlug)));
    }

    [Fact]
    public async Task InstallAsync_WhenActivationFails_RollsBackExistingPlugin()
    {
        using var fixture = new StoreInstallFixture();
        fixture.PluginActivation.ShouldLoad = false;

        var existingDirectory = Path.Combine(fixture.Paths.UserPluginsDirectory, "demo-plugin");
        Directory.CreateDirectory(existingDirectory);
        await File.WriteAllTextAsync(Path.Combine(existingDirectory, "existing.txt"), "keep");

        var zip = CreatePluginZip();
        var manifest = fixture.CreateManifest(zip);

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(existingDirectory, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(existingDirectory, "plugin.json")));
    }

    [Fact]
    public async Task InstallAsync_SolutionPackage_InstallsBundledResourcesAndRecordsSolution()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: true,
            includeAgent: true);
        var manifest = fixture.CreateManifest(zip, StoreAssetType.Solution, "demo-solution");

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill", "skill.md")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill", "assets", "template.md")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserAgentsDirectory, "solution-agent", "agent.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserSolutionsDirectory, "demo-solution", "solution.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserSolutionsDirectory, "demo-solution", "installed-assets.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserSolutionsDirectory, "demo-solution", "plugins")));
        Assert.Contains(fixture.PluginActivation.LoadedPaths, x => x.EndsWith("solution-plugin", StringComparison.OrdinalIgnoreCase));

        var installed = await fixture.AssetStore.LoadAsync();
        var record = Assert.Single(installed);
        Assert.Equal(StoreAssetType.Solution, record.AssetType);
        Assert.Equal("demo-solution", record.AssetSlug);
    }

    [Fact]
    public async Task InstallAsync_SolutionPackage_WithOnlySkill_ReturnsPassed()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreateSolutionZip(
            includePlugin: false,
            includeSkill: true,
            includeAgent: false);
        var manifest = fixture.CreateManifest(zip, StoreAssetType.Solution, "skill-only-solution");

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill", "skill.md")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserAgentsDirectory, "solution-agent")));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task InstallAsync_SolutionPackage_WithOptionalResourceCombinations_ReturnsPassed(
        bool includePlugin,
        bool includeSkill,
        bool includeAgent)
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreateSolutionZip(includePlugin, includeSkill, includeAgent);
        var manifest = fixture.CreateManifest(zip, StoreAssetType.Solution, "demo-solution");

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(includePlugin, Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin")));
        Assert.Equal(includeSkill, Directory.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill")));
        Assert.Equal(includeAgent, Directory.Exists(Path.Combine(fixture.Paths.UserAgentsDirectory, "solution-agent")));
    }

    [Fact]
    public async Task InstallAsync_SolutionPackage_UpdateAddsAndRemovesChildResources()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var firstZip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: false,
            includeAgent: false);
        var firstManifest = fixture.CreateManifest(firstZip, StoreAssetType.Solution, "demo-solution");
        var firstResult = await fixture.Service.InstallAsync(firstManifest);
        Assert.True(firstResult.Succeeded, firstResult.Message);

        var secondZip = CreateSolutionZip(
            includePlugin: false,
            includeSkill: true,
            includeAgent: true);
        var secondManifest = fixture.CreateManifest(secondZip, StoreAssetType.Solution, "demo-solution");
        var secondResult = await fixture.Service.InstallAsync(secondManifest);

        Assert.True(secondResult.Succeeded, secondResult.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.UserAgentsDirectory, "solution-agent")));

        var installedAssetsJson = await File.ReadAllTextAsync(Path.Combine(
            fixture.Paths.UserSolutionsDirectory,
            "demo-solution",
            "installed-assets.json"));
        var installedAssets = JsonSerializer.Deserialize<SolutionInstalledAssetsFile>(
            installedAssetsJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(installedAssets);
        Assert.Contains(installedAssets.History ?? [], x => x.Operation == "install" && x.Slug == "solution-plugin");
        Assert.Contains(installedAssets.History ?? [], x => x.Operation == "remove" && x.Slug == "solution-plugin");
        Assert.Contains(installedAssets.History ?? [], x => x.Operation == "install" && x.Slug == "solution-skill");
    }

    [Fact]
    public async Task InstallAsync_ExistingPlugin_UnloadsBeforeLoadingNewVersion()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var existingDirectory = Path.Combine(fixture.Paths.UserPluginsDirectory, "demo-plugin");
        Directory.CreateDirectory(existingDirectory);
        await File.WriteAllTextAsync(Path.Combine(existingDirectory, "old.txt"), "old");

        var zip = CreatePluginZip();
        var manifest = fixture.CreateManifest(zip);

        var result = await fixture.Service.InstallAsync(manifest);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["Unload:demo-plugin", "Load:demo-plugin"], fixture.PluginActivation.Operations);
        Assert.False(File.Exists(Path.Combine(existingDirectory, "old.txt")));
        Assert.True(File.Exists(Path.Combine(existingDirectory, "plugin.json")));
    }

    [Fact]
    public async Task UninstallAsync_Plugin_UnloadsBeforeDeletingDirectory()
    {
        using var fixture = new StoreInstallFixture();
        var pluginDirectory = Path.Combine(fixture.Paths.UserPluginsDirectory, "demo-plugin");
        Directory.CreateDirectory(pluginDirectory);
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "plugin.json"), "{}");
        var record = fixture.CreateInstalledAsset("version-1", "1.0.0", pluginDirectory);

        var result = await fixture.Service.UninstallAsync(record);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["Unload:demo-plugin"], fixture.PluginActivation.Operations);
        Assert.False(Directory.Exists(pluginDirectory));
        Assert.Empty(await fixture.AssetStore.LoadAsync());
    }

    [Fact]
    public async Task UninstallAsync_Solution_UnloadsPluginAndDeletesBundledResources()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var zip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: true,
            includeAgent: true);
        var manifest = fixture.CreateManifest(zip, StoreAssetType.Solution, "demo-solution");
        var installResult = await fixture.Service.InstallAsync(manifest);
        Assert.True(installResult.Succeeded, installResult.Message);
        fixture.PluginActivation.Operations.Clear();

        var result = await fixture.Service.UninstallAsync(installResult.InstalledAsset!);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["Unload:solution-plugin"], fixture.PluginActivation.Operations);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserSkillsDirectory, "solution-skill")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserAgentsDirectory, "solution-agent")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.UserSolutionsDirectory, "demo-solution")));
        Assert.Empty(await fixture.AssetStore.LoadAsync());
    }

    [Fact]
    public async Task UninstallAsync_Solution_WhenPluginReferencedByAnotherSolution_DoesNotDeletePlugin()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var firstZip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: false,
            includeAgent: false,
            solutionSlug: "first-solution");
        var secondZip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: false,
            includeAgent: false,
            solutionSlug: "second-solution");
        var firstManifest = fixture.CreateManifest(firstZip, StoreAssetType.Solution, "first-solution");
        var firstInstall = await fixture.Service.InstallAsync(firstManifest);
        var secondManifest = fixture.CreateManifest(secondZip, StoreAssetType.Solution, "second-solution");
        var secondInstall = await fixture.Service.InstallAsync(secondManifest);
        Assert.True(firstInstall.Succeeded, firstInstall.Message);
        Assert.True(secondInstall.Succeeded, secondInstall.Message);
        fixture.PluginActivation.Operations.Clear();

        var result = await fixture.Service.UninstallAsync(firstInstall.InstalledAsset!);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin")));
        Assert.Empty(fixture.PluginActivation.Operations);
    }

    [Fact]
    public async Task UninstallAsync_Solution_WhenPluginInstalledStandalone_DoesNotDeletePlugin()
    {
        using var fixture = new StoreInstallFixture();
        fixture.AccountStore.Session = fixture.CreateSession();
        var pluginDirectory = Path.Combine(fixture.Paths.UserPluginsDirectory, "solution-plugin");
        Directory.CreateDirectory(pluginDirectory);
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "plugin.json"), "{}");
        await fixture.AssetStore.UpsertAsync(
            fixture.CreateInstalledAsset(
                "version-standalone",
                "1.0.0",
                pluginDirectory,
                StoreAssetType.Plugin,
                "solution-plugin"));
        var zip = CreateSolutionZip(
            includePlugin: true,
            includeSkill: false,
            includeAgent: false);
        var manifest = fixture.CreateManifest(zip, StoreAssetType.Solution, "demo-solution");
        var installResult = await fixture.Service.InstallAsync(manifest);
        Assert.True(installResult.Succeeded, installResult.Message);
        fixture.PluginActivation.Operations.Clear();

        var result = await fixture.Service.UninstallAsync(installResult.InstalledAsset!);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(Directory.Exists(pluginDirectory));
        Assert.Empty(fixture.PluginActivation.Operations);
    }

    private static byte[] CreatePluginZip(bool includeDirectoryEntry = false)
        => CreateZip(
            includeDirectoryEntry ? ("tools/", string.Empty) : default,
            ("plugin.json", """
            {
              "id": "demo.plugin",
              "name": "Demo Plugin",
              "version": "1.0.0",
              "runtime": "process",
              "command": "demo.exe"
            }
            """),
            ("tools/demo.exe", "fake executable"));

    private static byte[] CreatePluginZipWithSymlink()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var pluginJson = archive.CreateEntry("plugin.json");
            using (var writer = new StreamWriter(pluginJson.Open(), Encoding.UTF8))
            {
                writer.Write("""
                {
                  "id": "demo.plugin",
                  "name": "Demo Plugin",
                  "version": "1.0.0",
                  "runtime": "process",
                  "command": "demo.exe"
                }
                """);
            }

            var link = archive.CreateEntry("demo.exe");
            link.ExternalAttributes = unchecked((int)((0xA000 | 0x1FF) << 16));
            using var linkWriter = new StreamWriter(link.Open(), Encoding.UTF8);
            linkWriter.Write("target");
        }

        return stream.ToArray();
    }

    private static byte[] CreateSolutionZip(
        bool includePlugin,
        bool includeSkill,
        bool includeAgent,
        string solutionSlug = "demo-solution")
    {
        var entries = new List<(string Name, string Content)>
        {
            ("solution.json", CreateSolutionJson(includePlugin, includeSkill, includeAgent, solutionSlug)),
            ("README.md", "Demo solution")
        };

        if (includePlugin)
        {
            entries.Add(("plugins/solution-plugin/plugin.json", """
            {
              "id": "solution.plugin",
              "name": "Solution Plugin",
              "version": "1.0.0",
              "runtime": "process",
              "command": "solution.exe"
            }
            """));
            entries.Add(("plugins/solution-plugin/tools/solution.exe", "fake executable"));
        }

        if (includeSkill)
        {
            entries.Add(("skills/solution-skill/skill.md", "# Solution Skill"));
            entries.Add(("skills/solution-skill/assets/template.md", "template"));
        }

        if (includeAgent)
        {
            entries.Add(("agents/solution-agent/agent.json", "{}"));
            entries.Add(("agents/solution-agent/prompt.md", "You are a solution agent."));
        }

        return CreateZip([.. entries]);
    }

    private static string CreateSolutionJson(
        bool includePlugin,
        bool includeSkill,
        bool includeAgent,
        string solutionSlug)
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

        return $$"""
        {
          "id": "demo.solution",
          "slug": "{{solutionSlug}}",
          "name": "Demo Solution",
          "version": "1.0.0",
          "description": "Demo",
          "assets": {
            {{plugins}},
            {{skills}},
            {{agents}}
          }
        }
        """;
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var entry = archive.CreateEntry(name);
                if (name.EndsWith('/'))
                {
                    continue;
                }

                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class StoreInstallFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cortana-store-tests", Guid.NewGuid().ToString("N"));

        public StoreInstallFixture()
        {
            Paths = new TestAppPaths(_root);
            AssetStore = new InstalledAssetStore(Paths);
            HttpClientFactory = new TestHttpClientFactory();
            PluginActivation = new TestPluginActivationService();
            AccountStore = new TestAccountStore();

            var services = new ServiceCollection()
                .AddLogging()
                .AddEventHub()
                .BuildServiceProvider();

            Service = new PackageInstallService(
                Paths,
                HttpClientFactory,
                new TestTargetResolver(Paths),
                AssetStore,
                PluginActivation,
                AccountStore,
                new TestBaseUrlProvider(),
                services.GetRequiredService<IPublisher>());
        }

        public TestAppPaths Paths { get; }

        public InstalledAssetStore AssetStore { get; }

        public TestHttpClientFactory HttpClientFactory { get; }

        public TestPluginActivationService PluginActivation { get; }

        public TestAccountStore AccountStore { get; }

        public PackageInstallService Service { get; }

        public StoreInstallManifest CreateManifest(
            byte[] packageBytes,
            StoreAssetType assetType = StoreAssetType.Plugin,
            string assetSlug = "demo-plugin")
        {
            HttpClientFactory.Content = packageBytes;
            return new StoreInstallManifest(
                "asset-1",
                assetSlug,
                assetType,
                "Demo Asset",
                "Netor",
                CreateVersion(packageBytes),
                "/api/v1/assets/asset-1/download",
                null);
        }

        public InstalledAssetRecord CreateInstalledAsset(
            string versionId,
            string versionName,
            string installedDirectory,
            StoreAssetType assetType = StoreAssetType.Plugin,
            string assetSlug = "demo-plugin")
            => new(
                "asset-1",
                assetSlug,
                assetType,
                "Demo Asset",
                versionId,
                versionName,
                "hash",
                1,
                installedDirectory,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

        public StoreInstallVersion CreateVersion(byte[] packageBytes)
            => new(
                "version-1",
                "1.0.0",
                "release",
                Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                "SHA256",
                packageBytes.Length);

        public PlatformAccountSession CreateSession()
            => new(
                "https://platform.local",
                "token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ApiAccountResponse("account-1", 1, "tester", "测试用户", "tester@example.com", "13800000000"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public byte[] Content { get; set; } = [];

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthorizationScheme { get; private set; }

        public HttpClient CreateClient(string name)
            => new(new Handler(
                () => Content,
                request =>
                {
                    LastRequestUri = request.RequestUri;
                    LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
                }));

        private sealed class Handler(Func<byte[]> contentProvider, Action<HttpRequestMessage> onRequest) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                onRequest(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(contentProvider())
                });
            }
        }
    }

    private sealed class TestBaseUrlProvider : IPlatformBaseUrlProvider
    {
        public string GetBaseUrl() => "https://platform.local";
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

    private sealed class TestTargetResolver(TestAppPaths appPaths) : IAssetInstallTargetResolver
    {
        public string ResolveRootDirectory(StoreAssetType assetType)
            => assetType switch
            {
                StoreAssetType.Plugin => appPaths.UserPluginsDirectory,
                StoreAssetType.Skill => appPaths.UserSkillsDirectory,
                StoreAssetType.Agent => appPaths.UserAgentsDirectory,
                StoreAssetType.Solution => appPaths.UserSolutionsDirectory,
                _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, null)
            };
    }

    private sealed class TestPluginActivationService : IPluginActivationService
    {
        public bool ShouldLoad { get; set; } = true;

        public List<string> LoadedPaths { get; } = [];

        public List<string> Operations { get; } = [];

        public void Unload(string directoryName)
        {
            Operations.Add($"Unload:{directoryName}");
        }

        public Task<bool> LoadByPathAsync(string pluginPath, CancellationToken cancellationToken = default)
        {
            LoadedPaths.Add(pluginPath);
            Operations.Add($"Load:{Path.GetFileName(pluginPath)}");
            return Task.FromResult(ShouldLoad);
        }
    }

    private sealed class TestAppPaths : IAppPaths
    {
        public TestAppPaths(string root)
        {
            UserDataDirectory = Path.Combine(root, "data");
            WorkspaceDirectory = Path.Combine(root, "workspace");
            WorkspaceSkillsDirectory = Path.Combine(WorkspaceDirectory, "skills");
            WorkspacePluginsDirectory = Path.Combine(WorkspaceDirectory, "plugins");
            UserSkillsDirectory = Path.Combine(UserDataDirectory, "skills");
            UserPluginsDirectory = Path.Combine(UserDataDirectory, "plugins");
            UserAgentsDirectory = Path.Combine(UserDataDirectory, "agents");
            UserSolutionsDirectory = Path.Combine(UserDataDirectory, "solutions");
            PluginDirectory = UserPluginsDirectory;
            WorkspaceResourcesDirectory = Path.Combine(WorkspaceDirectory, ".cortana", "resources");
            HistoryResourcesDirectory = Path.Combine(WorkspaceResourcesDirectory, "histories");
            PromptsDirectory = Path.Combine(UserDataDirectory, "prompts");
        }

        public string WorkspaceDirectory { get; }

        public string UserDataDirectory { get; }

        public string WorkspaceSkillsDirectory { get; }

        public string WorkspacePluginsDirectory { get; }

        public string UserSkillsDirectory { get; }

        public string UserPluginsDirectory { get; }

        public string UserAgentsDirectory { get; }

        public string UserSolutionsDirectory { get; }

        public string PluginDirectory { get; }

        public string WorkspaceResourcesDirectory { get; }

        public string HistoryResourcesDirectory { get; }

        public string PromptsDirectory { get; }
    }
}
