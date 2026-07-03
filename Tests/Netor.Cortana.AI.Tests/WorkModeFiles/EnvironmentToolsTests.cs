using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class EnvironmentToolsTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-environment-tools-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.WorkspaceDirectory);
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
    public async Task SetEnvironment_WritesEnvironmentYaml()
    {
        var fileService = new WorkTaskFileService(_paths);
        var tools = new EnvironmentTools(fileService, "task-tool");
        var function = tools.CreateSetEnvironmentTool();

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["valuesJson"] = "{\"output_dir\":\"novel/volume-1\",\"words\":8000}",
            ["notes"] = "保留大纲。"
        });

        Assert.Contains("环境已保存", result?.ToString() ?? string.Empty);

        var environment = fileService.LoadEnvironment("task-tool");
        Assert.IsNotNull(environment);
        Assert.AreEqual("novel/volume-1", environment.Values["output_dir"]);
        Assert.AreEqual("8000", environment.Values["words"]);
        Assert.AreEqual("保留大纲。", environment.Notes);
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");

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
