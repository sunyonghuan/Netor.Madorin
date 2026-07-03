using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Providers;

[TestClass]
public sealed class SkillDirectoryWatcherServiceTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;
    private ServiceProvider _services = null!;
    private ToolContextVersionService _version = null!;
    private SkillDirectoryWatcherService _watcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-skill-watcher-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        _version = new ToolContextVersionService();
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        _watcher = new SkillDirectoryWatcherService(
            _paths,
            _version,
            _services.GetRequiredService<ISubscriber>(),
            NullLogger<SkillDirectoryWatcherService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _watcher.Dispose();
        _services.Dispose();

        if (Directory.Exists(_root))
        {
            DeleteDirectoryWithRetry(_root);
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenUserSkillFileChanges_BumpsToolContextVersion()
    {
        await _watcher.StartAsync(CancellationToken.None);

        Directory.CreateDirectory(_paths.UserSkillsDirectory);
        File.WriteAllText(Path.Combine(_paths.UserSkillsDirectory, "skill.md"), "# skill");

        await WaitForVersionAsync(1);
    }

    [TestMethod]
    public async Task WorkspaceChanged_WhenNewWorkspaceSkillFileChanges_BumpsToolContextVersion()
    {
        await _watcher.StartAsync(CancellationToken.None);

        var newWorkspace = Path.Combine(_root, "workspace-2");
        await _services.GetRequiredService<IPublisher>()
            .PublishAsync(Events.OnWorkspaceChanged, new WorkspaceChangedArgs(newWorkspace));
        var newWorkspaceSkillsDirectory = Path.Combine(newWorkspace, ".cortana", "skills");
        await WaitForDirectoryAsync(newWorkspaceSkillsDirectory);

        File.WriteAllText(Path.Combine(newWorkspaceSkillsDirectory, "workspace-skill.md"), "# workspace skill");

        await WaitForVersionAsync(1);
    }

    private async Task WaitForVersionAsync(long expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_version.Current < expected)
        {
            await Task.Delay(50, cts.Token);
        }
    }

    private static async Task WaitForDirectoryAsync(string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!Directory.Exists(path))
        {
            await Task.Delay(50, cts.Token);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
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
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory { get; set; } = Path.Combine(root, "workspace");

        public string UserDataDirectory => Path.Combine(root, "data");

        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

        public string PluginDirectory => UserPluginsDirectory;

        public string WorkspaceResourcesDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "resources");

        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");

        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
