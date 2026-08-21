using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.Entitys;
using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.BuiltIn.FileBrowser;
using Netor.Madorin.Plugin.BuiltIn.PowerShell;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Delegation;

[TestClass]
public sealed class SystemToolCatalogServiceTests
{
    private string _root = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-tool-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var paths = new TestAppPaths(_root);
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton<IAppPaths>(paths)
            .AddSingleton<IHttpClientFactory, NoopHttpClientFactory>()
            .AddSingleton<IPluginRuntimeConfigProvider, NoopPluginRuntimeConfigProvider>()
            .AddSingleton<PluginManifestRegistry>()
            .AddSingleton<PluginLoader>()
            .BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [TestMethod]
    public async Task ValidateToolMountsAsync_WhenToolMissing_ReturnsMissingTool()
    {
        var service = new SystemToolCatalogService(
            [
                new FileBrowserProvider(
                    NullLogger<FileBrowserProvider>.Instance,
                    new FileBrowser(
                        _services.GetRequiredService<IAppPaths>(),
                        NullLogger<FileBrowser>.Instance))
            ],
            _services.GetRequiredService<PluginLoader>());

        var missing = await service.ValidateToolMountsAsync(
            ["sys_read_file", "missing_tool"],
            CancellationToken.None);

        Assert.HasCount(1, missing);
        Assert.AreEqual("missing_tool", missing[0]);
    }

    [TestMethod]
    public async Task ListAsync_WhenPluginToolContainsParameterSection_ReturnsParameterSummary()
    {
        var tool = AIFunctionFactory.Create(
            name: "demo_tool",
            method: () => "ok",
            description: "执行演示任务。\nParameters:\n- input: 输入内容\n- mode: 运行模式");

        var summary = SystemToolCatalogService.BuildParameterSummaryForTest(tool);

        StringAssert.Contains(summary, "Parameters:");
        StringAssert.Contains(summary, "input");
    }

    [TestMethod]
    public void BuildParameterSummaryForTest_WhenFunctionHasJsonSchema_ReturnsSchemaSummary()
    {
        var tool = AIFunctionFactory.Create(
            name: "schema_demo_tool",
            method: (string input, int limit, bool includeDetails) => "ok",
            description: "执行演示任务。");

        var summary = SystemToolCatalogService.BuildParameterSummaryForTest(tool);

        StringAssert.Contains(summary, "input: string");
        StringAssert.Contains(summary, "limit: integer");
        StringAssert.Contains(summary, "includeDetails: boolean");
        StringAssert.Contains(summary, "required");
    }

    [TestMethod]
    public async Task ListAsync_WhenPluginToolLoaded_ReturnsPluginToolCatalogItem()
    {
        var pluginLoader = _services.GetRequiredService<PluginLoader>();
        PluginLoaderTestHarness.AddPlugin(
            pluginLoader,
            _root,
            "test.plugin.catalog",
            "plugin_demo_tool",
            "plugin_hidden_tool");

        var service = new SystemToolCatalogService([], pluginLoader);

        var catalog = await service.ListAsync(CancellationToken.None);

        var item = catalog.Single(static tool => tool.ToolName == "plugin_demo_tool");
        Assert.AreEqual("plugin:test.plugin.catalog", item.Source);
        Assert.AreEqual("plugin", item.Category);
        Assert.AreEqual("general", item.Capability);
    }

    [TestMethod]
    public async Task ValidateToolMountsAsync_WhenUsingPowerShellCatalogTools_ReturnsNoMissing()
    {
        var service = new SystemToolCatalogService(
            [
                new PowerShellProvider(
                    new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance),
                    NullLogger<PowerShellProvider>.Instance,
                    new SessionRegistry(NullLogger<SessionRegistry>.Instance))
            ],
            _services.GetRequiredService<PluginLoader>());

        var missing = await service.ValidateToolMountsAsync(
            ["sys_execute_powershell", "sys_send_command", "sys_close_session"],
            CancellationToken.None);

        Assert.IsEmpty(missing);
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");
        public string UserDataDirectory => Path.Combine(root, "userdata");
        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        [Obsolete("工作区插件目录已废弃，请使用 UserPluginsDirectory。")]
        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");
        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");
        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");
        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");
        public string PluginDirectory => Path.Combine(UserDataDirectory, "plugins-bin");
        public string WorkspaceResourcesDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "resources");
        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");
        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }

    private sealed class NoopPluginRuntimeConfigProvider : IPluginRuntimeConfigProvider
    {
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest)
        {
            return "{}";
        }

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest)
        {
            return "{}";
        }
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }
}
