using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class WorkTaskReliabilityTests
{
    private const int LongWaitAttempts = 200;
    private const int LongWaitDelayMs = 25;

    private string _root = null!;
    private TestAppPaths _paths = null!;
    private CortanaDbContext _db = null!;
    private WorkTaskService _taskService = null!;
    private WorkTaskEventService _eventService = null!;
    private WorkExecutionLogService _logService = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-work-reliability-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.WorkspaceDirectory);
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _taskService = new WorkTaskService(_db);
        _eventService = new WorkTaskEventService(_db);
        _logService = new WorkExecutionLogService(_db, _taskService);
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();

        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                'session-1', 1, 1, 'workspace-1', '可靠性测试',
                '', '', 'agent-1', 0, 0,
                1, 0, '', 0)
            """);
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
    public void WorkTaskService_StoresOrchestratorStateAndFindsStaleTasks()
    {
        CreateTask("task-state");
        _taskService.SetManagerRunId("task-state", "run-a");
        _taskService.SetOrchestratorState("task-state", WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
        var staleHeartbeat = DateTimeOffset.Now.AddMinutes(-10).ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET OrchestratorHeartbeatAt = @Heartbeat WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Heartbeat", staleHeartbeat);
                cmd.Parameters.AddWithValue("@Id", "task-state");
            });

        var restored = _taskService.GetById("task-state");
        var staleTasks = _taskService.ListStaleRunningOrchestrators(DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeMilliseconds());

        Assert.IsNotNull(restored);
        Assert.AreEqual("run-a", restored.ManagerRunId);
        Assert.AreEqual(WorkTaskOrchestratorStates.Running, restored.OrchestratorState);
        Assert.HasCount(1, staleTasks);
        Assert.AreEqual("task-state", staleTasks[0].Id);
    }

    [TestMethod]
    public void WorkTaskEventService_CreatesListsAndMarksUnreadEvents()
    {
        CreateTask("task-event");
        var first = _eventService.Create("task-event", WorkTaskEventKinds.Milestone, "阶段完成", "run-a");
        _eventService.Create("task-event", WorkTaskEventKinds.Completion, "全部完成");

        var unread = _eventService.ListUnread("task-event", "run-a");
        _eventService.MarkRead([first.Id]);
        var remaining = _eventService.ListUnread("task-event", "run-a");

        Assert.HasCount(2, unread);
        Assert.AreEqual(WorkTaskEventKinds.Milestone, unread[0].Kind);
        Assert.HasCount(1, remaining);
        Assert.AreEqual(WorkTaskEventKinds.Completion, remaining[0].Kind);
    }

    [TestMethod]
    public void DbMigration_LinksOrphanWorkModeChatSessionsToSourceTask()
    {
        var dbPath = Path.Combine(_root, "orphan-work-session.db");
        using (var db = new CortanaDbContext(dbPath))
        {
            InsertChatSession(db, "main-session", "workspace-1", "工作任务主会话", 1000);
            InsertChatSession(db, "orphan-session", "workspace-1", "我要开发插件用于接入国内常用电商平台API接口,实现AI 自动化", 1200);
            db.Execute("""
                INSERT INTO ChatMessages (
                    Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName,
                    Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName)
                VALUES (
                    'message-orphan-tool', 1201, 1201, 'orphan-session', 'tool', '工具',
                    '[工具结果]\r\n调用ID: call_1\r\n结果: 步骤 [1.1 创建文件夹] 已开始执行。', '', 0, 'test', NULL, 'default', '默认助手')
                """);

            new WorkTaskService(db).Create(new WorkTaskEntity
            {
                Id = "task-orphan",
                SessionId = "main-session",
                WorkspaceId = "workspace-1",
                Title = "个人可接入电商平台API方案整理",
                InitialInput = "我要开发插件用于接入国内常用电商平台API接口,实现AI 自动化.\r\n请整理方案。",
                IsActive = true,
                Provider = "provider-1",
                Model = "model-1",
                AgentName = "default",
            });
        }

        using var migrated = new CortanaDbContext(dbPath);
        var sourceTaskId = migrated.ExecuteScalar<string>(
            "SELECT SourceTaskId FROM ChatSessions WHERE Id = 'orphan-session'");

        Assert.AreEqual("task-orphan", sourceTaskId);
    }

    [TestMethod]
    public async Task ProjectLeadService_PersistsMilestoneAndCompletionEvents()
    {
        CreateTask("task-events");
        _taskService.SetManagerRunId("task-events", "run-a");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-events",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "里程碑",
                    Role = "writer",
                    Input = "写第一段",
                    IsMilestone = true
                }
            ]
        });
        var service = new ProjectLeadService(
            fileService,
            new CompletingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);

        await service.RunAsync("task-events");

        var task = _taskService.GetById("task-events");
        var events = _eventService.ListUnread("task-events", "run-a");
        var logs = _logService.ListByTask("task-events");
        Assert.IsNotNull(task);
        Assert.IsTrue(task.IsActive);
        Assert.IsNotNull(task.CompletedAt);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.FinalReport));
        Assert.Contains("完成 s1", task.FinalReport);
        Assert.IsNull(task.OrchestratorState);
        Assert.HasCount(2, events);
        Assert.AreEqual(WorkTaskEventKinds.Milestone, events[0].Kind);
        Assert.AreEqual(WorkTaskEventKinds.Completion, events[1].Kind);
        Assert.HasCount(2, logs);
        Assert.AreEqual(WorkExecutionLogTypes.StepStart, logs[0].LogType);
        Assert.AreEqual(WorkExecutionLogTypes.StepComplete, logs[1].LogType);
    }

    [TestMethod]
    public async Task ProjectLeadService_PersistsStepLogsWithMainPhaseAndSubStepTitles()
    {
        CreateTask("task-step-log");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-step-log",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "step-01-01",
                    Title = "1.1 创建所有平台子文件夹",
                    Role = "default",
                    Input = """
                        主步骤：阶段一：创建文件夹结构
                        子步骤：1.1 创建所有平台子文件夹
                        验收标准：目录结构存在
                        """
                }
            ]
        });
        var service = new ProjectLeadService(
            fileService,
            new CompletingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);

        await service.RunAsync("task-step-log");

        var logs = _logService.ListByTask("task-step-log");
        var start = JsonSerializer.Deserialize(
            logs.Single(log => log.LogType == WorkExecutionLogTypes.StepStart).Content,
            WorkModeJsonContext.Default.StepStart);
        var complete = JsonSerializer.Deserialize(
            logs.Single(log => log.LogType == WorkExecutionLogTypes.StepComplete).Content,
            WorkModeJsonContext.Default.StepComplete);

        Assert.IsNotNull(start);
        Assert.IsNotNull(complete);
        Assert.AreEqual("阶段一：创建文件夹结构", start.Department);
        Assert.AreEqual("1.1 创建所有平台子文件夹", start.StepTitle);
        Assert.AreEqual("1.1 创建所有平台子文件夹", complete.StepTitle);
        Assert.AreNotEqual("default", start.Department);
    }

    [TestMethod]
    public async Task LongTask_SetPlanConfirmFinalizeRunsTwentyFiveStepsAndKeepsWorkRecordActive()
    {
        CreateTask("task-25-step-flow");
        _taskService.SetManagerRunId("task-25-step-flow", "run-25");
        var fileService = new WorkTaskFileService(_paths);
        var planTools = new PlanTools(_taskService, fileService, _publisher, "task-25-step-flow");
        var dispatcher = new CountingDispatcher();
        var projectLeadTools = CreateProjectLeadTools(fileService, "task-25-step-flow", dispatcher);

        var setPlanResult = await planTools.CreateSetPlanTool().InvokeAsync(new AIFunctionArguments
        {
            ["planJson"] = CreateTwentyFiveStepLegacyPlanJson()
        });

        Assert.Contains("计划已制定", setPlanResult?.ToString() ?? string.Empty);
        var draftedTask = _taskService.GetById("task-25-step-flow");
        Assert.IsNotNull(draftedTask);
        Assert.AreEqual(PlanTools.PlanConfirmationKind, draftedTask.PendingRequestKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(draftedTask.CurrentPlanJson));
        var draftedPlan = fileService.LoadPlan("task-25-step-flow");
        Assert.IsNotNull(draftedPlan);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, draftedPlan.Status);
        Assert.HasCount(25, draftedPlan.Steps);

        _taskService.SetPendingRequest("task-25-step-flow", null, null, null);
        SaveRequiredEnvironment(fileService, "task-25-step-flow");

        var finalizeResult = await projectLeadTools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("计划已交付项目组长执行，共 25 个步骤", finalizeResult?.ToString() ?? string.Empty);
        var completedPlan = await WaitForPlanStatusAsync(fileService, "task-25-step-flow", WorkTaskPlanStatuses.Done);
        var completedTask = await WaitForExecutionArchivedAsync("task-25-step-flow");
        var events = await WaitForUnreadEventsAsync("task-25-step-flow", "run-25", expectedCount: 26);
        var logs = _logService.ListByTask("task-25-step-flow");
        Assert.HasCount(25, completedPlan.Steps);
        Assert.IsTrue(completedPlan.Steps.All(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal)));
        Assert.HasCount(25, dispatcher.StepIds);
        CollectionAssert.AreEqual(Enumerable.Range(1, 25).Select(static index => $"step-{index:00}-01").ToArray(), dispatcher.StepIds);
        Assert.IsTrue(completedTask.IsActive);
        Assert.IsNotNull(completedTask.CompletedAt);
        Assert.IsNull(completedTask.OrchestratorState);
        Assert.IsFalse(string.IsNullOrWhiteSpace(completedTask.FinalReport));
        Assert.Contains("完成 step-25-01", completedTask.FinalReport);
        Assert.HasCount(26, events);
        Assert.AreEqual(25, events.Count(static item => string.Equals(item.Kind, WorkTaskEventKinds.Milestone, StringComparison.Ordinal)));
        Assert.AreEqual(WorkTaskEventKinds.Completion, events[^1].Kind);
        Assert.AreEqual(25, logs.Count(static item => item.LogType == WorkExecutionLogTypes.StepStart));
        Assert.AreEqual(25, logs.Count(static item => item.LogType == WorkExecutionLogTypes.StepComplete));
    }

    [TestMethod]
    public async Task ProjectLeadService_CompletesActiveTaskWhenNoExecutableStepsRemain()
    {
        CreateTask("task-fallback-complete");
        _taskService.SetManagerRunId("task-fallback-complete", "run-fallback");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-fallback-complete",
            Status = WorkTaskPlanStatuses.Running,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "已完成步骤",
                    Role = "writer",
                    Input = "无需再次执行",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = "先前已完成"
                }
            ]
        });
        var service = new ProjectLeadService(
            fileService,
            new FailingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);

        await service.RunAsync("task-fallback-complete");

        var task = _taskService.GetById("task-fallback-complete");
        var plan = fileService.LoadPlan("task-fallback-complete");
        var events = _eventService.ListUnread("task-fallback-complete", "run-fallback");
        Assert.IsNotNull(task);
        Assert.IsNotNull(plan);
        Assert.IsTrue(task.IsActive);
        Assert.IsNotNull(task.CompletedAt);
        Assert.IsFalse(string.IsNullOrWhiteSpace(task.FinalReport));
        Assert.Contains("先前已完成", task.FinalReport);
        Assert.IsNull(task.ErrorMessage);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, plan.Status);
        Assert.IsNull(task.OrchestratorState);
        Assert.HasCount(1, events);
        Assert.AreEqual(WorkTaskEventKinds.Completion, events[0].Kind);
    }

    [TestMethod]
    public async Task FinalReport_ReturnsPlanSnapshotWithoutMutatingTaskState()
    {
        CreateTask("task-report");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-report",
            Status = WorkTaskPlanStatuses.Done,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "写第一段",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = "已完成第一段"
                }
            ]
        });
        _taskService.MarkCompleted("task-report", "归档报告");
        var before = _taskService.GetById("task-report");
        Assert.IsNotNull(before);

        var tools = new PlanTools(_taskService, fileService, _publisher, "task-report");
        var result = await tools.CreateFinalReportTool().InvokeAsync(new AIFunctionArguments());

        var after = _taskService.GetById("task-report");
        Assert.IsNotNull(after);
        Assert.Contains("已完成第一段", result?.ToString() ?? string.Empty);
        Assert.AreEqual(before.IsActive, after.IsActive);
        Assert.AreEqual(before.CompletedAt, after.CompletedAt);
        Assert.AreEqual(before.FinalReport, after.FinalReport);
        Assert.AreEqual(before.OrchestratorState, after.OrchestratorState);
    }

    [TestMethod]
    public async Task FinalReport_ReturnsRunningSnapshotWithoutMutatingTaskState()
    {
        CreateTask("task-report-running");
        _taskService.SetOrchestratorState("task-report-running", WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-report-running",
            Status = WorkTaskPlanStatuses.Running,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "已完成步骤",
                    Role = "writer",
                    Input = "写第一段",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = "第一段完成"
                },
                new WorkTaskPlanStepFile
                {
                    Id = "s2",
                    Title = "待执行步骤",
                    Role = "writer",
                    Input = "写第二段",
                    Status = WorkTaskPlanStepStatuses.Pending
                }
            ]
        });
        var before = _taskService.GetById("task-report-running");
        Assert.IsNotNull(before);

        var tools = new PlanTools(_taskService, fileService, _publisher, "task-report-running");
        var result = await tools.CreateFinalReportTool().InvokeAsync(new AIFunctionArguments());

        var after = _taskService.GetById("task-report-running");
        Assert.IsNotNull(after);
        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("状态：running", text);
        Assert.Contains("进度：1/2 步完成", text);
        Assert.Contains("第一段完成", text);
        Assert.AreEqual(before.IsActive, after.IsActive);
        Assert.AreEqual(before.CompletedAt, after.CompletedAt);
        Assert.AreEqual(before.FinalReport, after.FinalReport);
        Assert.AreEqual(before.OrchestratorState, after.OrchestratorState);
    }

    [TestMethod]
    public async Task FinalReport_UsesCompactedDisplayTextWithoutMutatingArchivedReport()
    {
        CreateTask("task-report-compacted");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-report-compacted",
            Status = WorkTaskPlanStatuses.Done,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "写第一段",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = new string('甲', 1600)
                },
                new WorkTaskPlanStepFile
                {
                    Id = "s2",
                    Title = "步骤二",
                    Role = "writer",
                    Input = "写第二段",
                    Status = WorkTaskPlanStepStatuses.Done,
                    Summary = new string('乙', 1600)
                }
            ]
        });
        _taskService.MarkCompleted("task-report-compacted", "归档全文保持不变");
        var before = _taskService.GetById("task-report-compacted");
        Assert.IsNotNull(before);

        var tools = new PlanTools(
            _taskService,
            fileService,
            _publisher,
            "task-report-compacted",
            reportCompactionService: new StubReportCompactionService("短版工作总结"));
        var result = await tools.CreateFinalReportTool().InvokeAsync(new AIFunctionArguments());

        var after = _taskService.GetById("task-report-compacted");
        Assert.IsNotNull(after);
        Assert.AreEqual("短版工作总结", result?.ToString());
        Assert.AreEqual(before.FinalReport, after.FinalReport);
        Assert.AreEqual("归档全文保持不变", after.FinalReport);
    }

    [TestMethod]
    public async Task FinalizePlan_RejectsUnconfirmedPlan()
    {
        CreateTask("task-finalize-unconfirmed");
        _taskService.SetPendingRequest(
            "task-finalize-unconfirmed",
            "request-finalize",
            PlanTools.PlanConfirmationKind,
            "{}");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlanningPlan("task-finalize-unconfirmed"));
        var tools = CreateProjectLeadTools(fileService, "task-finalize-unconfirmed", new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("计划尚未经过用户确认", result?.ToString() ?? string.Empty);
        var task = _taskService.GetById("task-finalize-unconfirmed");
        Assert.IsNotNull(task);
        Assert.IsNull(task.OrchestratorState);
    }

    [TestMethod]
    public async Task FinalizePlan_RejectsNonPlanningPlanStatus()
    {
        CreateTask("task-finalize-running-plan");
        var fileService = new WorkTaskFileService(_paths);
        var plan = CreatePlanningPlan("task-finalize-running-plan");
        plan.Status = WorkTaskPlanStatuses.Running;
        fileService.SavePlan(plan);
        var tools = CreateProjectLeadTools(fileService, "task-finalize-running-plan", new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("计划当前状态是 running", result?.ToString() ?? string.Empty);
        Assert.AreEqual(WorkTaskPlanStatuses.Running, fileService.LoadPlan("task-finalize-running-plan")?.Status);
    }

    [TestMethod]
    public async Task FinalizePlan_RejectsRunningOrchestratorOnly()
    {
        await AssertFinalizeRejectsOrchestratorStateAsync(
            "task-finalize-orchestrator-running",
            WorkTaskOrchestratorStates.Running,
            "项目组长已在执行中");
    }

    [TestMethod]
    public async Task FinalizePlan_AllowsFreshPlanningPlanAfterPreviousTerminalState()
    {
        await AssertFinalizeAllowsPreviousTerminalStateAsync(
            "task-finalize-after-done",
            WorkTaskOrchestratorStates.Done);
        await AssertFinalizeAllowsPreviousTerminalStateAsync(
            "task-finalize-after-failed",
            WorkTaskOrchestratorStates.Failed);
        await AssertFinalizeAllowsPreviousTerminalStateAsync(
            "task-finalize-after-cancelled",
            WorkTaskOrchestratorStates.Cancelled);
    }

    [TestMethod]
    public async Task ProjectLeadService_MarksTaskFailedWhenDispatcherFails()
    {
        CreateTask("task-failed-close");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-failed-close",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "失败步骤",
                    Role = "writer",
                    Input = "触发失败"
                }
            ]
        });
        var service = new ProjectLeadService(
            fileService,
            new FailingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);

        await service.RunAsync("task-failed-close");

        var task = _taskService.GetById("task-failed-close");
        Assert.IsNotNull(task);
        Assert.IsTrue(task.IsActive);
        Assert.IsNotNull(task.CompletedAt);
        Assert.IsNull(task.OrchestratorState);
        Assert.AreEqual("派发失败", task.ErrorMessage);
    }

    [TestMethod]
    public async Task ProjectLeadTools_PauseAndCancelUpdateOrchestratorState()
    {
        CreateTask("task-control-state");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlanningPlan("task-control-state"));
        var tools = CreateProjectLeadTools(fileService, "task-control-state", new CompletingDispatcher());

        var pauseResult = await tools.CreatePauseOrchestratorTool().InvokeAsync(new AIFunctionArguments
        {
            ["reason"] = "等待用户修改"
        });

        Assert.Contains("编排器已暂停", pauseResult?.ToString() ?? string.Empty);
        var pausedPlan = fileService.LoadPlan("task-control-state");
        var pausedTask = _taskService.GetById("task-control-state");
        Assert.IsNotNull(pausedPlan);
        Assert.IsNotNull(pausedTask);
        Assert.AreEqual(WorkTaskPlanStatuses.Paused, pausedPlan.Status);
        Assert.AreEqual(WorkTaskOrchestratorStates.Paused, pausedTask.OrchestratorState);
        Assert.IsTrue(pausedTask.IsActive);

        var cancelResult = await tools.CreateCancelOrchestratorTool().InvokeAsync(new AIFunctionArguments
        {
            ["reason"] = "用户取消编排"
        });

        Assert.Contains("编排器已取消", cancelResult?.ToString() ?? string.Empty);
        var cancelledPlan = fileService.LoadPlan("task-control-state");
        var cancelledTask = _taskService.GetById("task-control-state");
        Assert.IsNotNull(cancelledPlan);
        Assert.IsNotNull(cancelledTask);
        Assert.AreEqual(WorkTaskPlanStatuses.Cancelled, cancelledPlan.Status);
        Assert.IsNull(cancelledTask.OrchestratorState);
    }

    [TestMethod]
    public async Task ProjectLeadTools_FinalizeRegistersOrchestratorCancellation()
    {
        CreateTask("task-runtime-cancel");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-runtime-cancel",
            Status = WorkTaskPlanStatuses.Planning,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "长步骤",
                    Role = "writer",
                    Input = "长时间执行"
                }
            ]
        });
        var registry = new WorkTaskCancellationRegistry();
        var dispatcher = new BlockingDispatcher();
        SaveRequiredEnvironment(fileService, "task-runtime-cancel");
        var tools = new ProjectLeadTools(
            fileService,
            new ProjectLeadService(
                fileService,
                dispatcher,
                _publisher,
                NullLogger<ProjectLeadService>.Instance,
                _taskService,
                _eventService,
                _logService),
            NullLogger<ProjectLeadTools>.Instance,
            "task-runtime-cancel",
            _taskService,
            registry);

        var finalizeResult = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());
        Assert.Contains("计划已交付项目组长执行", finalizeResult?.ToString() ?? string.Empty);

        Assert.IsTrue(await dispatcher.WaitStartedAsync());
        await registry.CancelAsync("task-runtime-cancel");

        var cancelledPlan = await WaitForPlanStatusAsync(fileService, "task-runtime-cancel", WorkTaskPlanStatuses.Cancelled);
        var cancelledTask = await WaitForExecutionArchivedAsync("task-runtime-cancel");
        Assert.AreEqual(WorkTaskPlanStatuses.Cancelled, cancelledPlan.Status);
        Assert.IsTrue(dispatcher.WasCancelled);
        Assert.IsTrue(cancelledTask.IsActive);
        Assert.IsNull(cancelledTask.OrchestratorState);
    }

    [TestMethod]
    public async Task RunningStepInterruptService_RewritesRunningStepAndRestartsExecution()
    {
        CreateTask("task-interrupt-step");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-interrupt-step",
            Status = WorkTaskPlanStatuses.Planning,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "先写旧内容"
                }
            ]
        });
        SaveRequiredEnvironment(fileService, "task-interrupt-step");

        var registry = new WorkTaskCancellationRegistry();
        var dispatcher = new BlockingDispatcher();
        var projectLeadService = new ProjectLeadService(
            fileService,
            dispatcher,
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);
        var interruptService = new RunningStepInterruptService(
            fileService,
            _taskService,
            _logService,
            registry,
            projectLeadService,
            NullLogger<ProjectLeadTools>.Instance);

        var tools = new ProjectLeadTools(
            fileService,
            projectLeadService,
            NullLogger<ProjectLeadTools>.Instance,
            "task-interrupt-step",
            _taskService,
            registry);

        var finalizeResult = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());
        Assert.Contains("计划已交付项目组长执行", finalizeResult?.ToString() ?? string.Empty);
        Assert.IsTrue(await dispatcher.WaitStartedAsync());

        var interrupted = await interruptService.InterruptCurrentStepAsync("task-interrupt-step", "改成新要求");
        Assert.IsTrue(interrupted);

        var plan = fileService.LoadPlan("task-interrupt-step");
        Assert.IsNotNull(plan);
        Assert.AreEqual(WorkTaskPlanStepStatuses.Pending, plan.Steps[0].Status);
        StringAssert.Contains(plan.Steps[0].Input, "改成新要求");
        Assert.IsTrue(dispatcher.WasCancelled);
    }

    [TestMethod]
    public async Task ProjectLeadWatchdogService_RestartsStaleRunningTask()
    {
        CreateTask("task-watchdog");
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(new WorkTaskPlanFile
        {
            TaskId = "task-watchdog",
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "写第一段"
                }
            ]
        });
        _taskService.SetOrchestratorState("task-watchdog", WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
        SaveRequiredEnvironment(fileService, "task-watchdog");
        _db.Execute(
            "UPDATE WorkTasks SET OrchestratorHeartbeatAt = @Heartbeat WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Heartbeat", DateTimeOffset.Now.AddMinutes(-10).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@Id", "task-watchdog");
            });
        var projectLead = new ProjectLeadService(
            fileService,
            new CompletingDispatcher(),
            _publisher,
            NullLogger<ProjectLeadService>.Instance,
            _taskService,
            _eventService,
            _logService);
        var watchdog = new ProjectLeadWatchdogService(
            _taskService,
            projectLead,
            NullLogger<ProjectLeadWatchdogService>.Instance,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMinutes(5));

        var restarted = await watchdog.ScanOnceAsync();
        var completedPlan = await WaitForPlanStatusAsync(fileService, "task-watchdog", WorkTaskPlanStatuses.Done);
        var completedTask = await WaitForExecutionArchivedAsync("task-watchdog");

        Assert.AreEqual(1, restarted);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, completedPlan.Status);
        Assert.IsNull(completedTask.OrchestratorState);
    }

    private async Task AssertFinalizeRejectsOrchestratorStateAsync(
        string taskId,
        string orchestratorState,
        string expectedMessage)
    {
        CreateTask(taskId);
        _taskService.SetOrchestratorState(taskId, orchestratorState, touchHeartbeat: false);
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlanningPlan(taskId));
        SaveRequiredEnvironment(fileService, taskId);
        var tools = CreateProjectLeadTools(fileService, taskId, new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains(expectedMessage, result?.ToString() ?? string.Empty);
        Assert.AreEqual(WorkTaskPlanStatuses.Planning, fileService.LoadPlan(taskId)?.Status);
    }

    private async Task AssertFinalizeAllowsPreviousTerminalStateAsync(
        string taskId,
        string orchestratorState)
    {
        CreateTask(taskId);
        _taskService.SetOrchestratorState(taskId, orchestratorState, touchHeartbeat: false);
        var fileService = new WorkTaskFileService(_paths);
        fileService.SavePlan(CreatePlanningPlan(taskId));
        SaveRequiredEnvironment(fileService, taskId);
        var tools = CreateProjectLeadTools(fileService, taskId, new CompletingDispatcher());

        var result = await tools.CreateFinalizePlanTool().InvokeAsync(new AIFunctionArguments());

        Assert.Contains("计划已交付项目组长执行", result?.ToString() ?? string.Empty);
        var completedPlan = await WaitForPlanStatusAsync(fileService, taskId, WorkTaskPlanStatuses.Done);
        var task = await WaitForExecutionArchivedAsync(taskId);
        Assert.AreEqual(WorkTaskPlanStatuses.Done, completedPlan.Status);
        Assert.IsNull(task.OrchestratorState);
    }

    private ProjectLeadTools CreateProjectLeadTools(
        WorkTaskFileService fileService,
        string taskId,
        IProjectStepDispatcher dispatcher)
    {
        return new ProjectLeadTools(
            fileService,
            new ProjectLeadService(
                fileService,
                dispatcher,
                _publisher,
                NullLogger<ProjectLeadService>.Instance,
                _taskService,
                _eventService,
                _logService),
            NullLogger<ProjectLeadTools>.Instance,
            taskId,
            _taskService);
    }

    private void CreateTask(string taskId)
    {
        var sessionId = $"{taskId}-session";
        InsertChatSession(_db, sessionId, "workspace-1", $"可靠性测试 {taskId}", DateTimeOffset.Now.ToUnixTimeMilliseconds());
        _taskService.Create(new WorkTaskEntity
        {
            Id = taskId,
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            Title = "可靠性测试",
            InitialInput = "开始",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = "agent-1"
        });
    }

    private static WorkTaskPlanFile CreatePlanningPlan(string taskId)
    {
        return new WorkTaskPlanFile
        {
            TaskId = taskId,
            Status = WorkTaskPlanStatuses.Planning,
            Steps =
            [
                new WorkTaskPlanStepFile
                {
                    Id = "s1",
                    Title = "步骤一",
                    Role = "writer",
                    Input = "写第一段"
                }
            ]
        };
    }

    private static string CreateTwentyFiveStepLegacyPlanJson()
    {
        var steps = Enumerable
            .Range(1, 25)
            .Select(static index => new WorkPlanMainStep(
                $"电商平台调研步骤 {index:00}",
                [new WorkPlanSubStep(
                    $"调研平台能力 {index:00}",
                    $"输出平台能力清单 {index:00}")]))
            .ToList();
        var plan = new WorkPlan(steps);
        return JsonSerializer.Serialize(plan, WorkModeJsonContext.Default.WorkPlan);
    }

    private static void SaveRequiredEnvironment(WorkTaskFileService fileService, string taskId)
    {
        fileService.SaveEnvironment(new WorkTaskEnvironmentFile
        {
            TaskId = taskId,
            Values =
            {
                ["workspace_dir"] = ".",
                ["task_goal"] = "完成可靠性测试任务"
            }
        });
    }

    private static void InsertChatSession(CortanaDbContext db, string id, string workspaceId, string title, long timestamp)
    {
        db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                @Id, @Timestamp, @Timestamp, @WorkspaceId, @Title,
                '', '', 'default', 0, 0,
                @Timestamp, 0, '', 0)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Timestamp", timestamp);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Title", title);
            });
    }

    private static async Task<WorkTaskPlanFile> WaitForPlanStatusAsync(
        WorkTaskFileService fileService,
        string taskId,
        string status)
    {
        for (var i = 0; i < LongWaitAttempts; i++)
        {
            var plan = fileService.LoadPlan(taskId);
            if (plan is not null && string.Equals(plan.Status, status, StringComparison.Ordinal))
            {
                if (!string.Equals(status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal)
                    || plan.Steps.All(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal)))
                {
                    return plan;
                }
            }

            await Task.Delay(LongWaitDelayMs);
        }

        return fileService.LoadPlan(taskId)
            ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
    }

    private async Task<WorkTaskEntity> WaitForTaskOrchestratorStateAsync(string taskId, string state)
    {
        for (var i = 0; i < LongWaitAttempts; i++)
        {
            var task = _taskService.GetById(taskId);
            if (task is not null
                && string.Equals(task.OrchestratorState, state, StringComparison.Ordinal)
                && task.CompletedAt is not null)
            {
                return task;
            }

            await Task.Delay(LongWaitDelayMs);
        }

        return _taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
    }

    private async Task<WorkTaskEntity> WaitForExecutionArchivedAsync(string taskId)
    {
        for (var i = 0; i < LongWaitAttempts; i++)
        {
            var task = _taskService.GetById(taskId);
            if (task is not null
                && task.CompletedAt is not null
                && task.OrchestratorState is null)
            {
                return task;
            }

            await Task.Delay(LongWaitDelayMs);
        }

        return _taskService.GetById(taskId)
            ?? throw new InvalidOperationException($"工作任务不存在：{taskId}");
    }

    private async Task<List<WorkTaskEventEntity>> WaitForUnreadEventsAsync(
        string taskId,
        string runId,
        int expectedCount)
    {
        List<WorkTaskEventEntity> events = [];
        for (var i = 0; i < LongWaitAttempts; i++)
        {
            events = _eventService.ListUnread(taskId, runId);
            if (events.Count >= expectedCount
                || events.Any(static item => string.Equals(item.Kind, WorkTaskEventKinds.Completion, StringComparison.Ordinal)))
            {
                return events;
            }

            await Task.Delay(LongWaitDelayMs);
        }

        return events;
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

    private sealed class CountingDispatcher : IProjectStepDispatcher
    {
        public List<string> StepIds { get; } = [];

        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            StepIds.Add(step.Id);
            return Task.FromResult($"完成 {step.Id}");
        }
    }

    private sealed class CompletingDispatcher : IProjectStepDispatcher
    {
        public Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            return Task.FromResult($"完成 {step.Id}");
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

    private sealed class StubReportCompactionService(string result) : IWorkTaskReportCompactionService
    {
        public Task<string?> CompactAsync(string taskId, string rawReport, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(result);
    }

    private sealed class BlockingDispatcher : IProjectStepDispatcher
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public async Task<string> DispatchAsync(
            string taskId,
            WorkTaskPlanStepFile step,
            WorkTaskEnvironmentFile? environment,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "不应完成";
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }

        public async Task<bool> WaitStartedAsync()
        {
            var completed = await Task.WhenAny(_started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            return completed == _started.Task;
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
