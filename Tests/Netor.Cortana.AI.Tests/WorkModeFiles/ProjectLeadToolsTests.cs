using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class ProjectLeadToolsTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-project-lead-tools-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.WorkspaceDirectory);
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
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FinalizePlan_WritesPlanAndStartsProjectLead()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlan("task-finalize"));
        SaveRequiredEnvironment(fileService, "task-finalize");
        var tools = CreateTools(fileService, "task-finalize", new CompletingDispatcher());
        var function = tools.CreateFinalizePlanTool();

        var result = await function.InvokeAsync(new AIFunctionArguments());

        Assert.Contains("计划已交付项目组长执行", result?.ToString() ?? string.Empty);

        var plan = await WaitForPlanStatusAsync(fileService, "task-finalize", WorkTaskPlanStatuses.Done);
        Assert.HasCount(1, plan.Steps);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, plan.Steps[0].Status);
        Assert.AreEqual("完成 s1", plan.Steps[0].Summary);
    }

    [TestMethod]
    public async Task FinalizePlan_RequiresExistingPlanFile()
    {
        var fileService = new WorkTaskFileService(_paths);
        var tools = CreateTools(fileService, "task-missing-plan", new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("尚未制定计划", result?.ToString() ?? string.Empty);
    }

    [TestMethod]
    public async Task FinalizePlan_RequiresEnvironment()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlan("task-missing-environment"));
        var tools = CreateTools(fileService, "task-missing-environment", new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("尚未设置任务环境", result?.ToString() ?? string.Empty);
    }

    [TestMethod]
    public async Task PauseAndCancelOrchestrator_UpdatePlanStatus()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlan("task-control"));
        var tools = CreateTools(fileService, "task-control", new CompletingDispatcher());

        var pauseResult = await tools.CreatePauseOrchestratorTool().InvokeAsync(new AIFunctionArguments
        {
            ["reason"] = "等待用户确认"
        });

        Assert.Contains("编排器已暂停", pauseResult?.ToString() ?? string.Empty);
        var pausedPlan = fileService.LoadPlan("task-control");
        Assert.IsNotNull(pausedPlan);
        Assert.AreEqual(WorkTaskPlanStatuses.Paused, pausedPlan.Status);

        var cancelResult = await tools.CreateCancelOrchestratorTool().InvokeAsync(new AIFunctionArguments
        {
            ["reason"] = "用户取消"
        });

        Assert.Contains("编排器已取消", cancelResult?.ToString() ?? string.Empty);
        var cancelledPlan = fileService.LoadPlan("task-control");
        Assert.IsNotNull(cancelledPlan);
        Assert.AreEqual(WorkTaskPlanStatuses.Cancelled, cancelledPlan.Status);
    }

    [TestMethod]
    public async Task ResumeOrchestrator_StartsPausedPlan()
    {
        var fileService = new WorkTaskFileService(_paths);
        var plan = CreatePlan("task-resume");
        plan.Status = WorkTaskPlanStatuses.Paused;
        fileService.SavePlan(plan);
        var tools = CreateTools(fileService, "task-resume", new CompletingDispatcher());

        var result = await tools.CreateResumeOrchestratorTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("编排器已恢复", result?.ToString() ?? string.Empty);
        var completedPlan = await WaitForPlanStatusAsync(fileService, "task-resume", WorkTaskPlanStatuses.Done);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, completedPlan.Steps[0].Status);
    }

    [TestMethod]
    public async Task UpdatePlan_ReplacesPendingStepsAndKeepsDoneSteps()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-update",
            Status = WorkTaskPlanStatuses.Running,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "done",
                    Title = "已完成",
                    Role = "writer",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = "旧摘要"
                },
                new WorkTaskPlanStepFile
                {
                    Id = "old-pending",
                    Title = "旧待办",
                    Role = "writer",
                    Status = WorkTaskPlanStepStatuses.Pending
                }
            ]
        });
        var tools = CreateTools(fileService, "task-update", new CompletingDispatcher());

        var result = await tools.CreateUpdatePlanTool().InvokeAsync(new AIFunctionArguments
        {
            ["updateJson"] = """
                {
                  "steps": [
                    {
                      "id": "new-pending",
                      "title": "新待办",
                      "role": "writer",
                      "input": "改成悲剧结局"
                    }
                  ]
                }
                """
        });

        Assert.Contains("计划已更新", result?.ToString() ?? string.Empty);
        var updatedPlan = fileService.LoadPlan("task-update");
        Assert.IsNotNull(updatedPlan);
        Assert.AreEqual(WorkTaskPlanStatuses.Paused, updatedPlan.Status);
        Assert.HasCount(2, updatedPlan.Steps);
        Assert.AreEqual("done", updatedPlan.Steps[0].Id);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, updatedPlan.Steps[0].Status);
        Assert.AreEqual("new-pending", updatedPlan.Steps[1].Id);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Pending, updatedPlan.Steps[1].Status);
    }

    [TestMethod]
    public async Task UpdatePlan_RejectsDoneStepOverwrite()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-protect",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "done",
                    Title = "已完成",
                    Status = WorkTaskPlanStepStatuses.Done
                }
            ]
        });
        var tools = CreateTools(fileService, "task-protect", new CompletingDispatcher());

        var result = await tools.CreateUpdatePlanTool().InvokeAsync(new AIFunctionArguments
        {
            ["updateJson"] = """
                {
                  "steps": [
                    {
                      "id": "done",
                      "title": "覆盖已完成",
                      "role": "writer",
                      "input": "不允许"
                    }
                  ]
                }
                """
        });

        Assert.Contains("不能通过 update_plan 覆盖", result?.ToString() ?? string.Empty);
        var plan = fileService.LoadPlan("task-protect");
        Assert.IsNotNull(plan);
        Assert.AreEqual("已完成", plan.Steps[0].Title);
    }

    [TestMethod]
    public async Task UpdatePlan_WritesExpandableStepFields()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-expandable-tool",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "old-pending",
                    Title = "旧待办",
                    Role = "writer",
                    Status = WorkTaskPlanStepStatuses.Pending
                }
            ]
        });
        var tools = CreateTools(fileService, "task-expandable-tool", new CompletingDispatcher());

        var result = await tools.CreateUpdatePlanTool().InvokeAsync(new AIFunctionArguments
        {
            ["updateJson"] = """
                {
                  "steps": [
                    {
                      "id": "phase-1",
                      "title": "第一阶段",
                      "role": "dotnet-developer",
                      "input": "先调查后展开",
                      "expandable": true,
                      "expander_role": "planner"
                    }
                  ]
                }
                """
        });

        Assert.Contains("计划已更新", result?.ToString() ?? string.Empty);
        var plan = fileService.LoadPlan("task-expandable-tool");
        Assert.IsNotNull(plan);
        Assert.AreEqual("phase-1", plan.Steps[0].Id);
        Assert.IsTrue(plan.Steps[0].IsExpandable);
        Assert.AreEqual("planner", plan.Steps[0].ExpanderRole);
    }

    [TestMethod]
    public async Task RunAsync_ReturnsWhenPlanIsCancelled()
    {
        var fileService = new WorkTaskFileService(_paths);
        var plan = CreatePlan("task-cancelled");
        plan.Status = WorkTaskPlanStatuses.Cancelled;
        fileService.SavePlan(plan);
        var dispatcher = new CompletingDispatcher();
        var service = CreateProjectLeadService(fileService, dispatcher);

        await service.RunAsync("task-cancelled");

        var restored = fileService.LoadPlan("task-cancelled");
        Assert.IsNotNull(restored);
        Assert.AreEqual(WorkTaskPlanStatuses.Cancelled, restored.Status);
        Assert.AreEqual(0, dispatcher.DispatchCount);
    }

    private ProjectLeadTools CreateTools(
        WorkTaskFileService fileService,
        string taskId,
        IProjectStepDispatcher dispatcher)
    {
        return new ProjectLeadTools(
            fileService,
            CreateProjectLeadService(fileService, dispatcher),
            NullLogger<ProjectLeadTools>.Instance,
            taskId);
    }

    private ProjectLeadService CreateProjectLeadService(
        WorkTaskFileService fileService,
        IProjectStepDispatcher dispatcher)
    {
        return new ProjectLeadService(
            fileService,
            dispatcher,
            _publisher,
            NullLogger<ProjectLeadService>.Instance);
    }

    private static WorkTaskPlanFile CreatePlan(string taskId)
    {
        return new WorkTaskPlanFile
        {
            TaskId = taskId,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "写第一段",
                    Status = WorkTaskPlanStepStatuses.Pending
                }
            ]
        };
    }

    private static void SaveRequiredEnvironment(WorkTaskFileService fileService, string taskId)
    {
        fileService.SaveEnvironment(new WorkTaskEnvironmentFile
        {
            TaskId = taskId,
            Values =
            {
                ["workspace_dir"] = ".",
                ["task_goal"] = "完成测试任务"
            }
        });
    }

    private static async Task<WorkTaskPlanFile> WaitForPlanStatusAsync(
        WorkTaskFileService fileService,
        string taskId,
        string status)
    {
        for (var i = 0; i < 50; i++)
        {
            var plan = fileService.LoadPlan(taskId);
            if (plan is not null && string.Equals(plan.Status, status, StringComparison.Ordinal))
            {
                return plan;
            }

            await Task.Delay(20);
        }

        return fileService.LoadPlan(taskId)
            ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
    }

    private sealed class CompletingDispatcher : IProjectStepDispatcher
    {
        public int DispatchCount { get; private set; }

        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            DispatchCount++;
            return Task.FromResult($"完成 {step.Id}");
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
