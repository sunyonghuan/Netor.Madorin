using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Tests.AgentFiles;

[TestClass]
public sealed class AgentFileInfrastructureTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-agent-files-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.UserDataDirectory);
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
    public void Serializer_RoundTripsManifestSchema()
    {
        var serializer = new AgentManifestSerializer();
        var manifest = new AgentManifest
        {
            Name = "novel-writer",
            DisplayName = "小说作家",
            Description = "按章节大纲撰写小说正文",
            Kind = AgentManifestKinds.Agent,
            Avatar = "assets/avatar.png",
            Enabled = true,
            SortOrder = 10,
            AllowWorkflowMemory = false,
            BoundPlugins = ["independent-station-ops"],
            BoundMcp = ["novel-toolkit-mcp"],
        };

        var yaml = serializer.Serialize(manifest);
        var restored = serializer.Deserialize(yaml);

        Assert.AreEqual(manifest.Name, restored.Name);
        Assert.AreEqual(manifest.DisplayName, restored.DisplayName);
        Assert.AreEqual(manifest.Description, restored.Description);
        Assert.AreEqual(manifest.Kind, restored.Kind);
        Assert.AreEqual(manifest.Avatar, restored.Avatar);
        Assert.AreEqual(manifest.Enabled, restored.Enabled);
        Assert.AreEqual(manifest.SortOrder, restored.SortOrder);
        Assert.AreEqual(manifest.AllowWorkflowMemory, restored.AllowWorkflowMemory);
        CollectionAssert.AreEqual(manifest.BoundPlugins, restored.BoundPlugins);
        CollectionAssert.AreEqual(manifest.BoundMcp, restored.BoundMcp);
    }

    [TestMethod]
    public void Validator_RejectsMissingRequiredFields()
    {
        var validator = new AgentManifestValidator();

        var result = validator.Validate(new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "",
        }, "default");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Error, "description");
    }

    [TestMethod]
    public void Validator_RejectsAvatarPathEscapingManifestDirectory()
    {
        var validator = new AgentManifestValidator();

        var result = validator.Validate(new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "默认助手说明",
            Avatar = "../avatar.png",
        }, "default");

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Error, "manifest 所在目录内");
    }

    [TestMethod]
    public void Index_Rebuild_LoadsOnlyValidManifestDirectories()
    {
        var serializer = new AgentManifestSerializer();
        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "默认助手说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "默认提示词");
        WriteAgent("broken", serializer, new AgentManifest
        {
            Name = "other-name",
            DisplayName = "错误",
            Description = "目录名不一致",
        }, "错误提示词");

        var index = CreateIndex(serializer);
        index.Rebuild(_paths.UserAgentsDirectory);

        var records = index.GetAll();
        Assert.HasCount(1, records);
        Assert.AreEqual("default", records[0].Manifest.Name);
        Assert.AreEqual("默认提示词", records[0].Prompt);
    }

    [TestMethod]
    public void SeedFromFactory_CopiesMissingSeedsWithoutOverwritingExistingAgent()
    {
        var serializer = new AgentManifestSerializer();
        var seedRoot = Path.Combine(_paths.UserDataDirectory, "agents-seed");
        WriteAgent(seedRoot, "default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "工厂默认说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "工厂提示词");
        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "用户默认助手",
            Description = "用户已修改说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "用户提示词");

        var service = new AgentSeedFromFactoryService(_paths, serializer, NullLogger<AgentSeedFromFactoryService>.Instance);
        service.EnsureSeedAgents();

        var prompt = File.ReadAllText(Path.Combine(_paths.UserAgentsDirectory, "default", AgentFileService.PromptFileName));
        Assert.AreEqual("用户提示词", prompt);
    }

    [TestMethod]
    public void SeedFromFactory_BackfillsMissingAvatarWithoutOverwritingPrompt()
    {
        var serializer = new AgentManifestSerializer();
        var seedRoot = Path.Combine(_paths.UserDataDirectory, "agents-seed");
        var seedDirectory = Path.Combine(seedRoot, "default");
        WriteAgent(seedRoot, "default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "工厂默认说明",
            Kind = AgentManifestKinds.BuiltinSystem,
            Avatar = "assets/avatar.png",
        }, "工厂提示词");
        Directory.CreateDirectory(Path.Combine(seedDirectory, "assets"));
        File.WriteAllText(Path.Combine(seedDirectory, "assets", "avatar.png"), "avatar");

        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "用户默认助手",
            Description = "用户已修改说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "用户提示词");

        var service = new AgentSeedFromFactoryService(_paths, serializer, NullLogger<AgentSeedFromFactoryService>.Instance);
        service.EnsureSeedAgents();

        var targetDirectory = Path.Combine(_paths.UserAgentsDirectory, "default");
        var prompt = File.ReadAllText(Path.Combine(targetDirectory, AgentFileService.PromptFileName));
        var manifest = serializer.Deserialize(File.ReadAllText(Path.Combine(targetDirectory, AgentFileService.ManifestFileName)));

        Assert.AreEqual("用户提示词", prompt);
        Assert.AreEqual("assets/avatar.png", manifest.Avatar);
        Assert.IsTrue(File.Exists(Path.Combine(targetDirectory, "assets", "avatar.png")));
    }

    [TestMethod]
    public void SeedFromFactory_BackfillsLegacyDefaultDisplayNameOnly()
    {
        var serializer = new AgentManifestSerializer();
        var seedRoot = Path.Combine(_paths.UserDataDirectory, "agents-seed");
        WriteAgent(seedRoot, "default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "董秘小月",
            Description = "工厂默认说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "工厂提示词");
        WriteAgent(seedRoot, "custom", serializer, new AgentManifest
        {
            Name = "custom",
            DisplayName = "种子自定义",
            Description = "工厂自定义说明",
            Kind = AgentManifestKinds.Agent,
        }, "工厂自定义提示词");

        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "用户已修改说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "用户提示词");
        WriteAgent("custom", serializer, new AgentManifest
        {
            Name = "custom",
            DisplayName = "用户自定义",
            Description = "用户自定义说明",
            Kind = AgentManifestKinds.Agent,
        }, "用户自定义提示词");

        var service = new AgentSeedFromFactoryService(_paths, serializer, NullLogger<AgentSeedFromFactoryService>.Instance);
        service.EnsureSeedAgents();

        var defaultManifest = serializer.Deserialize(File.ReadAllText(Path.Combine(_paths.UserAgentsDirectory, "default", AgentFileService.ManifestFileName)));
        var customManifest = serializer.Deserialize(File.ReadAllText(Path.Combine(_paths.UserAgentsDirectory, "custom", AgentFileService.ManifestFileName)));
        var defaultPrompt = File.ReadAllText(Path.Combine(_paths.UserAgentsDirectory, "default", AgentFileService.PromptFileName));

        Assert.AreEqual("董秘小月", defaultManifest.DisplayName);
        Assert.AreEqual("用户自定义", customManifest.DisplayName);
        Assert.AreEqual("用户提示词", defaultPrompt);
    }

    [TestMethod]
    public async Task Watcher_DebouncesManifestChangesAndRefreshesIndex()
    {
        var serializer = new AgentManifestSerializer();
        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "原说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "原提示词");

        var index = CreateIndex(serializer);
        index.Rebuild(_paths.UserAgentsDirectory);
        using var watcher = new AgentFileWatcher(index, NullLogger<AgentFileWatcher>.Instance);
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.AgentChanged += (_, name) => changed.TrySetResult(name);
        watcher.Start(_paths.UserAgentsDirectory);

        WriteAgent("default", serializer, new AgentManifest
        {
            Name = "default",
            DisplayName = "默认助手",
            Description = "新说明",
            Kind = AgentManifestKinds.BuiltinSystem,
        }, "新提示词");

        var completed = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.AreSame(changed.Task, completed);
        Assert.AreEqual("default", await changed.Task);
        Assert.AreEqual("新说明", index.GetByName("default")?.Manifest.Description);
    }

    private AgentFileIndex CreateIndex(AgentManifestSerializer serializer) =>
        new(serializer, new AgentManifestValidator(), NullLogger<AgentFileIndex>.Instance);

    private void WriteAgent(string name, AgentManifestSerializer serializer, AgentManifest manifest, string prompt) =>
        WriteAgent(_paths.UserAgentsDirectory, name, serializer, manifest, prompt);

    private static void WriteAgent(string root, string name, AgentManifestSerializer serializer, AgentManifest manifest, string prompt)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AgentFileService.ManifestFileName), serializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(directory, AgentFileService.PromptFileName), prompt);
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
