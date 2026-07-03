using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class WorkPlanDoubleWriteTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;
    private CortanaDbContext _db = null!;
    private WorkTaskService _taskService = null!;
    private WorkPlanTemplateService _templateService = null!;
    private WorkTaskFileService _fileService = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-plan-double-write-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.WorkspaceDirectory);
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _taskService = new WorkTaskService(_db);
        _templateService = new WorkPlanTemplateService(_db);
        _fileService = new WorkTaskFileService(_paths);
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
    public async Task SetPlan_WritesCurrentPlanJsonPlanYamlAndConfirmationRequest()
    {
        CreateTask("task-set-plan");
        _taskService.SetOrchestratorState("task-set-plan", WorkTaskOrchestratorStates.Done, touchHeartbeat: true);
        _taskService.SetPendingRequest("task-set-plan", "old-request", "old-kind", "{}");
        var tools = new PlanTools(_taskService, _fileService, _publisher, "task-set-plan");

        var result = await tools.CreateSetPlanTool().InvokeAsync(new AIFunctionArguments
        {
            ["planJson"] = CreateLegacyPlanJson()
        });

        Assert.Contains("计划已制定", result?.ToString() ?? string.Empty);

        var task = _taskService.GetById("task-set-plan");
        Assert.IsNotNull(task);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.CurrentPlanJson));
        Assert.AreEqual(PlanTools.PlanConfirmationKind, task.PendingRequestKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.PendingRequestId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.PendingRequestData));
        Assert.IsNull(task.OrchestratorState);
        Assert.IsNull(task.OrchestratorHeartbeatAt);

        var cachedPlan = JsonSerializer.Deserialize(task.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
        Assert.IsNotNull(cachedPlan);
        Assert.HasCount(1, cachedPlan.MainSteps);
        Assert.HasCount(2, cachedPlan.MainSteps[0].SubSteps);

        var planFile = _fileService.LoadPlan("task-set-plan");
        Assert.IsNotNull(planFile);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, planFile.Status);
        Assert.HasCount(2, planFile.Steps);
        Assert.AreEqual("step-01-01", planFile.Steps[0].Id);
        Assert.AreEqual("默认专员", planFile.Steps[0].Role);
        Assert.Contains("验收标准：输出完整清单", planFile.Steps[0].Input);
        Assert.IsTrue(planFile.Steps[1].IsMilestone);
        Assert.IsTrue(File.Exists(_fileService.GetPlanPath("task-set-plan")));
    }

    [TestMethod]
    public void TryClearPendingRequest_ClearsOnceOnly_WhenRequestIdMatches()
    {
        CreateTask("task-pending-once");
        _taskService.SetPendingRequest("task-pending-once", "request-1", PlanTools.PlanConfirmationKind, "{}");

        var first = _taskService.TryClearPendingRequest("task-pending-once", "request-1");
        var second = _taskService.TryClearPendingRequest("task-pending-once", "request-1");

        var task = _taskService.GetById("task-pending-once");
        Assert.AreEqual(1, first);
        Assert.AreEqual(0, second);
        Assert.IsNotNull(task);
        Assert.IsNull(task.PendingRequestId);
        Assert.IsNull(task.PendingRequestKind);
        Assert.IsNull(task.PendingRequestData);
    }

    [TestMethod]
    public async Task LoadPlanFromTemplate_WritesCurrentPlanJsonPlanYamlAndConfirmationRequest()
    {
        CreateTask("task-template");
        _taskService.SetOrchestratorState("task-template", WorkTaskOrchestratorStates.Failed, touchHeartbeat: true);
        _taskService.SetPendingRequest("task-template", "old-request", "old-kind", "{}");
        _templateService.Create(new WorkPlanTemplateEntity
        {
            Id = "template-research",
            Name = "调研模板",
            Description = "用于双写验证",
            Category = "test",
            PlanJson = CreateLegacyPlanJson(),
            Scope = "workspace",
            WorkspaceId = "workspace-1"
        });
        var tools = new PlanTemplateTools(_taskService, _templateService, _fileService, _publisher, "task-template");

        var result = await tools.CreateLoadPlanFromTemplateTool().InvokeAsync(new AIFunctionArguments
        {
            ["nameOrId"] = "template-research"
        });

        Assert.Contains("已加载计划模板", result?.ToString() ?? string.Empty);

        var task = _taskService.GetById("task-template");
        Assert.IsNotNull(task);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.CurrentPlanJson));
        Assert.AreEqual(PlanTools.PlanConfirmationKind, task.PendingRequestKind);
        Assert.IsNull(task.OrchestratorState);
        Assert.IsNull(task.OrchestratorHeartbeatAt);

        var planFile = _fileService.LoadPlan("task-template");
        Assert.IsNotNull(planFile);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, planFile.Status);
        Assert.HasCount(2, planFile.Steps);
        Assert.AreEqual("1.1 收集平台资料", planFile.Steps[0].Title);
        Assert.AreEqual("默认专员", planFile.Steps[0].Role);
        Assert.IsTrue(File.Exists(_fileService.GetPlanPath("task-template")));

        var template = _templateService.GetById("template-research");
        Assert.IsNotNull(template);
        Assert.AreEqual(1, template.UseCount);
        Assert.IsNotNull(template.LastUsedAt);
    }

    [TestMethod]
    public async Task SaveCurrentPlanAsTemplate_ReadsCurrentPlanJsonCompatibilityCache()
    {
        CreateTask("task-save-template");
        _taskService.UpdatePlan("task-save-template", CreateLegacyPlanJson());
        var tools = new PlanTemplateTools(_taskService, _templateService, _fileService, _publisher, "task-save-template");

        var result = await tools.CreateSaveCurrentPlanAsTemplateTool().InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "保存模板验证",
            ["description"] = "验证 CurrentPlanJson 兼容读端",
            ["category"] = "compat",
            ["scope"] = "workspace"
        });

        Assert.Contains("已保存计划模板", result?.ToString() ?? string.Empty);
        var templates = _templateService.Search("保存模板验证", "compat", "workspace-1", take: 10);
        Assert.HasCount(1, templates);
        Assert.AreEqual("task-save-template", templates[0].SourceTaskId);
        Assert.AreEqual("workspace", templates[0].Scope);
        Assert.AreEqual("workspace-1", templates[0].WorkspaceId);
        var savedPlan = JsonSerializer.Deserialize(templates[0].PlanJson, WorkModeJsonContext.Default.WorkPlan);
        Assert.IsNotNull(savedPlan);
        Assert.HasCount(1, savedPlan.MainSteps);
    }

    [TestMethod]
    public async Task RecentCompletedTaskPlan_ReadsCurrentPlanJsonAndSetsSourceTask()
    {
        CreateTask("task-recent-source");
        _taskService.UpdatePlan("task-recent-source", CreateLegacyPlanJson());
        _taskService.MarkCompleted("task-recent-source", "已归档");
        CreateTask("task-recent-current");
        _taskService.SetSourceTask("task-recent-current", "task-recent-source");
        var tools = new RecentTaskTools(_taskService, "task-recent-current");

        var result = await tools.CreateGetRecentCompletedTaskPlanTool().InvokeAsync(new AIFunctionArguments());

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("source_task_id: task-recent-source", text);
        Assert.Contains("收集平台资料", text);
        Assert.Contains("验收标准：输出完整清单", text);
        var current = _taskService.GetById("task-recent-current");
        Assert.IsNotNull(current);
        Assert.AreEqual("task-recent-source", current.SourceTaskId);
    }

    [TestMethod]
    public void HandoffPresetPlan_CanCreatePlanYamlWithMentionedAgentRole()
    {
        var task = new WorkTaskEntity
        {
            Id = "task-handoff",
            SessionId = "task-handoff-session",
            WorkspaceId = "workspace-1",
            Title = "转交任务",
            InitialInput = "执行讨论好的方案",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = "general-manager",
            MentionsJson = JsonSerializer.Serialize(
                new List<AgentMentionDto>
                {
                    new("writer-v2", "写作专员")
                },
                WorkModeJsonContext.Default.ListAgentMentionDto)
        };
        var plan = JsonSerializer.Deserialize(CreateLegacyPlanJson(), WorkModeJsonContext.Default.WorkPlan);
        Assert.IsNotNull(plan);

        _fileService.SavePlan(WorkPlanLegacyConverter.ToPlanFile(plan, task));

        var planFile = _fileService.LoadPlan("task-handoff");
        Assert.IsNotNull(planFile);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, planFile.Status);
        Assert.HasCount(2, planFile.Steps);
        Assert.AreEqual("写作专员", planFile.Steps[0].Role);
        Assert.AreEqual("写作专员", planFile.Steps[1].Role);
        Assert.IsTrue(File.Exists(_fileService.GetPlanPath("task-handoff")));
    }

    private void CreateTask(string taskId)
    {
        InsertChatSession(_db, $"{taskId}-session", "workspace-1", $"双写测试 {taskId}");
        _taskService.Create(new WorkTaskEntity
        {
            Id = taskId,
            SessionId = $"{taskId}-session",
            WorkspaceId = "workspace-1",
            Title = "双写测试",
            InitialInput = "开始",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = "默认专员"
        });
    }

    private static string CreateLegacyPlanJson()
    {
        return """
            {
              "main_steps": [
                {
                  "title": "平台调研",
                  "sub_steps": [
                    {
                      "title": "收集平台资料",
                      "acceptance_criteria": "输出完整清单"
                    },
                    {
                      "title": "整理对比结论",
                      "acceptance_criteria": "形成对比表"
                    }
                  ]
                }
              ]
            }
            """;
    }

    private static void InsertChatSession(CortanaDbContext db, string id, string workspaceId, string title)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                @Id, @Now, @Now, @WorkspaceId, @Title,
                '', '', 'default', 0, 0,
                @Now, 0, '', 0)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Title", title);
            });
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
