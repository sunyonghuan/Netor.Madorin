using System.Reflection;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class ProjectStepDispatcherPromptTests
{
    private string _root = null!;
    private CortanaDbContext _db = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-step-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            DeleteDirectoryWithRetry(_root);
        }
    }

    [TestMethod]
    public void BuildStepInput_IncludesEnvironmentAndOnlyCurrentStepInput()
    {
        var step = new WorkTaskPlanStepFile
        {
            Id = "s2",
            Title = "第二步",
            Role = "writer",
            Input = "只写第二步内容"
        };
        var environment = new WorkTaskEnvironmentFile
        {
            TaskId = "task-1",
            Values =
            {
                ["output_dir"] = "drafts",
                ["style"] = "冷峻克制"
            },
            Notes = "保留用户原始约束。"
        };

        var prompt = InvokeBuildStepInput(step, environment);

        Assert.Contains("output_dir: drafts", prompt);
        Assert.Contains("style: 冷峻克制", prompt);
        Assert.Contains("notes: 保留用户原始约束。", prompt);
        Assert.Contains("只写第二步内容", prompt);
        Assert.Contains("必须调用真实工具", prompt);
        Assert.Contains("禁止在文本中模拟工具调用", prompt);
        Assert.DoesNotContain("第一步内容", prompt);
        Assert.DoesNotContain("第三步内容", prompt);
    }

    [TestMethod]
    public void ResolveAgentFromMentions_PrefersMentionedAgentId()
    {
        var agentService = CreateAgentService();
        agentService.Add(new AgentEntity
        {
            Id = "global-writer",
            Name = "novel-writer-v2",
            Description = "同名全局 Agent。",
            Instructions = "global"
        });
        agentService.Add(new AgentEntity
        {
            Id = "mentioned-writer",
            Name = "被 @ 的写作专员",
            Description = "被明确提及的 Agent。",
            Instructions = "mentioned"
        });
        var dispatcher = CreateDispatcher(agentService);
        var mentionsJson = JsonSerializer.Serialize(
            new List<AgentMentionDto>
            {
                new("mentioned-writer", "novel-writer-v2")
            },
            WorkModeJsonContext.Default.ListAgentMentionDto);

        var resolved = InvokeResolveAgentFromMentions(dispatcher, mentionsJson, "novel-writer-v2");

        Assert.IsNotNull(resolved);
        Assert.AreEqual("mentioned-writer", resolved.Id);
        Assert.AreEqual("被 @ 的写作专员", resolved.Name);
    }

    [TestMethod]
    public void ResolveAgentFromMentions_ReturnsNullWhenRoleIsNotMentioned()
    {
        var agentService = CreateAgentService();
        agentService.Add(new AgentEntity
        {
            Id = "mentioned-writer",
            Name = "被 @ 的写作专员",
            Description = "被明确提及的 Agent。",
            Instructions = "mentioned"
        });
        var dispatcher = CreateDispatcher(agentService);
        var mentionsJson = JsonSerializer.Serialize(
            new List<AgentMentionDto>
            {
                new("mentioned-writer", "novel-writer-v2")
            },
            WorkModeJsonContext.Default.ListAgentMentionDto);

        var resolved = InvokeResolveAgentFromMentions(dispatcher, mentionsJson, "code-reviewer");

        Assert.IsNull(resolved);
    }

    private static string InvokeBuildStepInput(WorkTaskPlanStepFile step, WorkTaskEnvironmentFile environment)
    {
        var method = typeof(ProjectStepDispatcher).GetMethod(
            "BuildStepInput",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        var result = method.Invoke(null, [step, environment]);
        Assert.IsInstanceOfType(result, typeof(string));
        return (string)result;
    }

    private AgentService CreateAgentService()
    {
        var serializer = new AgentManifestSerializer();
        var validator = new AgentManifestValidator();
        var index = new AgentFileIndex(serializer, validator, NullLogger<AgentFileIndex>.Instance);
        var fileService = new AgentFileService(
            new TestAppPaths(_root),
            serializer,
            validator,
            index,
            NullLogger<AgentFileService>.Instance);
        fileService.RebuildIndex();
        return new AgentService(fileService, new SystemSettingsService(_db));
    }

    private ProjectStepDispatcher CreateDispatcher(AgentService agentService)
    {
        return new ProjectStepDispatcher(
            new WorkTaskService(_db),
            agentService,
            null!,
            null!,
            null!,
            new WorkExecutionLogService(_db, new WorkTaskService(_db)),
            _publisher,
            NullLogger<ProjectStepDispatcher>.Instance);
    }

    private static AgentEntity? InvokeResolveAgentFromMentions(
        ProjectStepDispatcher dispatcher,
        string mentionsJson,
        string role)
    {
        var method = typeof(ProjectStepDispatcher).GetMethod(
            "ResolveAgentFromMentions",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method);
        var result = method.Invoke(dispatcher, [mentionsJson, role]);
        return (AgentEntity?)result;
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(100);
            }
        }
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
