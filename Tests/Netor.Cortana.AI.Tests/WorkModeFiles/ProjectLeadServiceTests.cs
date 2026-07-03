using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class ProjectLeadServiceTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-project-lead-{Guid.NewGuid():N}");
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
    public async Task RunAsync_CompletesPendingStepsInOrder()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SaveEnvironment(new WorkTaskEnvironmentFile
        {
            TaskId = "task-run",
            Values = { ["output_dir"] = "drafts" }
        });
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-run",
            Steps =
            [
                new WorkTaskPlanStepFile { Id = "s1", Title = "步骤一", Role = "writer", Input = "写第一段" },
                new WorkTaskPlanStepFile { Id = "s2", Title = "步骤二", Role = "writer", Input = "写第二段" }
            ]
        });

        var dispatcher = new CapturingDispatcher();
        var service = new ProjectLeadService(
            fileService,
            dispatcher,
            _publisher,
            NullLogger<ProjectLeadService>.Instance);

        await service.RunAsync("task-run");

        var plan = fileService.LoadPlan("task-run");
        Assert.IsNotNull(plan);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, plan.Status);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, plan.Steps[0].Status);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, plan.Steps[1].Status);
        Assert.AreEqual("完成 s1", plan.Steps[0].Summary);
        Assert.AreEqual("完成 s2", plan.Steps[1].Summary);
        CollectionAssert.AreEqual(new[] { "s1", "s2" }, dispatcher.StepIds);
        Assert.AreEqual("drafts", dispatcher.EnvironmentValues[0]["output_dir"]);
    }

    [TestMethod]
    public async Task RunAsync_MarksPlanFailed_WhenDispatcherFails()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-fail",
            Steps =
            [
                new WorkTaskPlanStepFile { Id = "s1", Title = "步骤一", Role = "writer", Input = "写第一段" }
            ]
        });

        var service = new ProjectLeadService(
            fileService,
            new FailingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance);

        await service.RunAsync("task-fail");

        var plan = fileService.LoadPlan("task-fail");
        Assert.IsNotNull(plan);
        Assert.AreEqual(WorkTaskPlanStatuses.Failed, plan.Status);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Failed, plan.Steps[0].Status);
        Assert.AreEqual(1, plan.Steps[0].Attempts);
        Assert.AreEqual("派发失败", plan.Steps[0].LastError);
    }

    [TestMethod]
    public async Task RunAsync_ExpandsStepRunsChildrenAndCollapsesParent()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-expand",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "phase-1",
                    Title = "第一阶段",
                    Role = "dotnet-developer",
                    Input = "先拆分阶段，再执行子任务。",
                    IsExpandable = true,
                    ExpanderRole = "planner"
                },
                new WorkTaskPlanStepFile
                {
                    Id = "final",
                    Title = "最终整理",
                    Role = "writer",
                    Input = "整理结果。"
                }
            ]
        });

        var dispatcher = new ExpandingDispatcher();
        var service = new ProjectLeadService(
            fileService,
            dispatcher,
            _publisher,
            NullLogger<ProjectLeadService>.Instance);

        await service.RunAsync("task-expand");

        var plan = fileService.LoadPlan("task-expand");
        Assert.IsNotNull(plan);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, plan.Status);
        Assert.HasCount(4, plan.Steps);
        Assert.AreEqual("phase-1", plan.Steps[0].Id);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, plan.Steps[0].Status);
        Assert.IsNotNull(plan.Steps[0].ExpandedAt);
        Assert.AreEqual("phase-1-01", plan.Steps[1].Id);
        Assert.AreEqual("phase-1", plan.Steps[1].ParentStepId);
        Assert.AreEqual(1, plan.Steps[1].Depth);
        Assert.AreEqual("phase-1-02", plan.Steps[2].Id);
        Assert.AreEqual("final", plan.Steps[3].Id);
        CollectionAssert.AreEqual(new[] { "phase-1:planner", "phase-1-01:writer", "phase-1-02:writer", "final:writer" }, dispatcher.Calls);
    }

    [TestMethod]
    public async Task RunAsync_StopsAfterCurrentStepWhenPlanIsPaused()
    {
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-pause-after-step",
            Steps =
            [
                new WorkTaskPlanStepFile { Id = "s1", Title = "步骤一", Role = "writer", Input = "写第一段" },
                new WorkTaskPlanStepFile { Id = "s2", Title = "步骤二", Role = "writer", Input = "写第二段" }
            ]
        });
        var dispatcher = new PausingDispatcher(fileService);
        var service = new ProjectLeadService(
            fileService,
            dispatcher,
            _publisher,
            NullLogger<ProjectLeadService>.Instance);

        await service.RunAsync("task-pause-after-step");

        var plan = fileService.LoadPlan("task-pause-after-step");
        Assert.IsNotNull(plan);
        Assert.AreEqual(WorkTaskPlanStatuses.Paused, plan.Status);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, plan.Steps[0].Status);
        Assert.AreEqual("完成 s1 后暂停", plan.Steps[0].Summary);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Pending, plan.Steps[1].Status);
        CollectionAssert.AreEqual(new[] { "s1" }, dispatcher.StepIds);
    }

    private sealed class CapturingDispatcher : IProjectStepDispatcher
    {
        public List<string> StepIds { get; } = [];

        public List<Dictionary<string, string>> EnvironmentValues { get; } = [];

        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            StepIds.Add(step.Id);
            EnvironmentValues.Add(environment?.Values ?? []);
            return Task.FromResult($"完成 {step.Id}");
        }
    }

    private sealed class PausingDispatcher(WorkTaskFileService fileService) : IProjectStepDispatcher
    {
        public List<string> StepIds { get; } = [];

        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            StepIds.Add(step.Id);
            var plan = fileService.LoadPlan(taskId)
                ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
            plan.Status = WorkTaskPlanStatuses.Paused;
            fileService.SavePlan(plan);
            return Task.FromResult($"完成 {step.Id} 后暂停");
        }
    }

    private sealed class FailingDispatcher : IProjectStepDispatcher
    {
        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("派发失败");
        }
    }

    private sealed class ExpandingDispatcher : IProjectStepDispatcher
    {
        public List<string> Calls { get; } = [];

        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            Calls.Add($"{step.Id}:{step.Role}");
            if (string.Equals(step.Role, "planner", StringComparison.Ordinal))
            {
                return Task.FromResult("""
                    ```json
                    {
                      "steps": [
                        {
                          "id": "phase-1-01",
                          "title": "子步骤一",
                          "role": "writer",
                          "input": "执行第一项"
                        },
                        {
                          "id": "phase-1-02",
                          "title": "子步骤二",
                          "role": "writer",
                          "input": "执行第二项"
                        }
                      ]
                    }
                    ```
                    """);
            }

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
