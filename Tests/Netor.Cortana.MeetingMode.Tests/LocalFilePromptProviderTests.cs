using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class LocalFilePromptProviderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-prompt-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetPromptAsync_WhenPublishAndUserFilesExist_PrefersPublishDirectory()
    {
        var appBase = Path.Combine(_root, "publish");
        var userData = Path.Combine(_root, "user");
        WritePrompt(appBase, "meeting", "host.md", "发布目录提示词");
        WritePrompt(Path.Combine(userData, "prompts"), "meeting", "host.md", "用户目录提示词");
        var provider = new LocalFilePromptProvider(new TestAppPaths(userData), appBase);

        var prompt = await provider.GetPromptAsync("meeting.host");

        Assert.AreEqual("发布目录提示词", prompt);
    }

    [TestMethod]
    public async Task GetPromptAsync_WhenPublishFileMissing_FallsBackToUserDirectory()
    {
        var appBase = Path.Combine(_root, "publish");
        var userData = Path.Combine(_root, "user");
        WritePrompt(Path.Combine(userData, "prompts"), "meeting", "host.md", "用户目录提示词");
        var provider = new LocalFilePromptProvider(new TestAppPaths(userData), appBase);

        var prompt = await provider.GetPromptAsync("meeting.host");

        Assert.AreEqual("用户目录提示词", prompt);
    }

    private static void WritePrompt(string root, string directory, string fileName, string content)
    {
        var dir = Path.Combine(root, "prompts", directory);
        if (root.EndsWith("prompts", StringComparison.OrdinalIgnoreCase))
        {
            dir = Path.Combine(root, directory);
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private sealed class TestAppPaths(string userDataDirectory) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(userDataDirectory, "workspace");

        public string UserDataDirectory => userDataDirectory;

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
