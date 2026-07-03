using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class WorkTaskFileServiceTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-work-task-files-{Guid.NewGuid():N}");
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
    public void SaveAndLoadPlan_RoundTripsYamlFile()
    {
        var service = new WorkTaskFileService(_paths);
        var plan = new WorkTaskPlanFile
        {
            TaskId = "task-001",
            Status = WorkTaskPlanStatuses.Planning,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "ch-01",
                    Title = "第一章 起源",
                    Role = "novel-writer",
                    Input = "章节大纲：\n- 主角醒来\n- 发现星舰失联",
                    IsMilestone = true,
                    IsExpandable = true,
                    ExpanderRole = "planner",
                    ParentStepId = "volume-01",
                    Depth = 1,
                    ExpandedAt = DateTimeOffset.Parse("2026-06-07T10:00:00Z"),
                }
            ]
        };

        service.SavePlan(plan);

        var restored = service.LoadPlan("task-001");
        Assert.IsNotNull(restored);
        Assert.AreEqual("task-001", restored.TaskId);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, restored.Status);
        Assert.HasCount(1, restored.Steps);
        Assert.AreEqual("ch-01", restored.Steps[0].Id);
        Assert.AreEqual("novel-writer", restored.Steps[0].Role);
        Assert.AreEqual(plan.Steps[0].Input, restored.Steps[0].Input);
        Assert.IsTrue(restored.Steps[0].IsMilestone);
        Assert.IsTrue(restored.Steps[0].IsExpandable);
        Assert.AreEqual("planner", restored.Steps[0].ExpanderRole);
        Assert.AreEqual("volume-01", restored.Steps[0].ParentStepId);
        Assert.AreEqual(1, restored.Steps[0].Depth);
        Assert.IsNotNull(restored.Steps[0].ExpandedAt);
        Assert.IsTrue(File.Exists(Path.Combine(_paths.WorkspaceDirectory, ".cortana", "work-tasks", "task-001", "plan.yaml")));
    }

    [TestMethod]
    public void SavePlan_RejectsExpandableStepWithoutExpanderRole()
    {
        var service = new WorkTaskFileService(_paths);
        var plan = new WorkTaskPlanFile
        {
            TaskId = "task-invalid-expandable",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "phase-1",
                    Title = "阶段一",
                    IsExpandable = true
                }
            ]
        };

        Assert.ThrowsExactly<InvalidDataException>(() => service.SavePlan(plan));
    }

    [TestMethod]
    public void SaveAndLoadEnvironment_RoundTripsValuesAndNotes()
    {
        var service = new WorkTaskFileService(_paths);
        var environment = new WorkTaskEnvironmentFile
        {
            TaskId = "task-env",
            Values =
            {
                ["output_dir"] = "novel/volume-1",
                ["tone"] = "冷峻、克制"
            },
            Notes = "用户要求保留原始大纲。\n不要覆盖旧文件。"
        };

        service.SaveEnvironment(environment);

        var restored = service.LoadEnvironment("task-env");
        Assert.IsNotNull(restored);
        Assert.AreEqual("task-env", restored.TaskId);
        Assert.AreEqual("novel/volume-1", restored.Values["output_dir"]);
        Assert.AreEqual("冷峻、克制", restored.Values["tone"]);
        Assert.AreEqual(environment.Notes, restored.Notes);
    }

    [TestMethod]
    public void UpdateStepStatus_UpdatesSummaryAndMarksPlanDone()
    {
        var service = new WorkTaskFileService(_paths);
        service.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-done",
            Steps =
            [
                new WorkTaskPlanStepFile { Id = "s1", Title = "步骤一" }
            ]
        });

        var updated = service.UpdateStepStatus("task-done", "s1", WorkTaskPlanStepStatuses.Done, "已完成第一步。");

        Assert.AreEqual(WorkTaskPlanStatuses.Done, updated.Status);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Done, updated.Steps[0].Status);
        Assert.AreEqual("已完成第一步。", updated.Steps[0].Summary);

        var restored = service.LoadPlan("task-done");
        Assert.IsNotNull(restored);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, restored.Status);
    }

    [TestMethod]
    public void GetTaskDirectory_RejectsTraversalTaskId()
    {
        var service = new WorkTaskFileService(_paths);

        Assert.ThrowsExactly<ArgumentException>(() => service.GetTaskDirectory("../outside"));
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
