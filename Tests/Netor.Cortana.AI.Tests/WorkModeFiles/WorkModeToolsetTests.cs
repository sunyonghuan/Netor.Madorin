using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class WorkModeToolsetTests
{
    private string _root = null!;
    private TestAppPaths _paths = null!;
    private CortanaDbContext _db = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-work-toolset-{Guid.NewGuid():N}");
        _paths = new TestAppPaths(_root);
        Directory.CreateDirectory(_paths.WorkspaceDirectory);
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                'session-logs', 1, 1, 'workspace', '日志测试',
                '', '', 'agent', 0, 0,
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
    public void GetAllTools_DoesNotExposeExecutionOrSubAgentTools()
    {
        var toolset = CreateToolset("task-toolset");

        var toolNames = toolset.GetAllTools()
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "set_plan",
                "get_plan",
                "final_report",
                "set_environment",
                "finalize_plan",
                "pause_orchestrator",
                "resume_orchestrator",
                "cancel_orchestrator",
                "update_plan",
                "list_plan_templates",
                "load_plan_from_template",
                "save_current_plan_as_template",
                "load_plan_from_chat_history",
                "load_plan_from_groupchat",
                "get_recent_completed_task_plan",
                "get_execution_logs",
                "ask_user",
                "check_pending_user_input",
                "cancel_task",
                "close_task_record"
            },
            toolNames.ToArray());

        var toolNameArray = toolNames.ToArray();
        CollectionAssert.DoesNotContain(toolNameArray, "dispatch_step");
        CollectionAssert.DoesNotContain(toolNameArray, "dispatch_parallel");
        CollectionAssert.DoesNotContain(toolNameArray, "finish_parallel_block");
        CollectionAssert.DoesNotContain(toolNameArray, "dispatch_nested_workflow");
        CollectionAssert.DoesNotContain(toolNameArray, "finish_nested_workflow");
        CollectionAssert.DoesNotContain(toolNameArray, "verify_step");
        CollectionAssert.DoesNotContain(toolNameArray, "send_back");
        CollectionAssert.DoesNotContain(toolNameArray, "self_evaluate");
        CollectionAssert.DoesNotContain(toolNameArray, "start_subagent_background");
        CollectionAssert.DoesNotContain(toolNameArray, "wait_for_subagent");
        CollectionAssert.DoesNotContain(toolNameArray, "cancel_subagent");
    }

    [TestMethod]
    public async Task GeneralManagerPrompt_DeclaredTools_AreRegisteredForWorkModeManager()
    {
        var promptProvider = new EmbeddedResourcePromptProvider(typeof(GeneralManagerAgentBuilder).Assembly);
        var prompt = await promptProvider.GetPromptAsync("work_mode.general_manager");
        Assert.IsFalse(string.IsNullOrWhiteSpace(prompt));

        var declaredToolNames = ExtractAvailableToolNames(prompt!);
        Assert.IsTrue(declaredToolNames.Contains("finalize_plan", StringComparer.Ordinal));

        var toolset = CreateToolset("task-prompt-contract");
        var managerTools = toolset.GetAllTools()
            .Concat(CreateFileTools());
        var availableToolNames = ToolFilter
            .Apply(managerTools, ToolFilterMode.WorkModeManager)
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = declaredToolNames
            .Where(name => !availableToolNames.Contains(name))
            .ToArray();

        CollectionAssert.AreEquivalent(Array.Empty<string>(), missing);
    }

    [TestMethod]
    public void WorkModeManagerFilter_AllowsBasicWorkspaceFileTools()
    {
        var toolNames = ToolFilter
            .Apply(CreateFileTools(), ToolFilterMode.WorkModeManager)
            .Select(static tool => tool.Name)
            .ToArray();

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "sys_list_directory",
                "sys_get_file_info",
                "sys_read_file",
                "sys_search_files",
                "sys_create_file",
                "sys_write_file",
                "sys_write_large_file",
                "sys_write_files_batch",
                "sys_edit_file",
                "sys_create_directory"
            },
            toolNames);

        CollectionAssert.DoesNotContain(toolNames, "sys_delete_file");
        CollectionAssert.DoesNotContain(toolNames, "sys_move_file");
        CollectionAssert.DoesNotContain(toolNames, "sys_delete_directory");
    }

    [TestMethod]
    public async Task GetExecutionLogs_ReturnsRealStepAndToolTrace()
    {
        var taskService = new WorkTaskService(_db);
        taskService.Create(new WorkTaskEntity
        {
            Id = "task-logs",
            SessionId = "session-logs",
            WorkspaceId = "workspace",
            Title = "日志任务",
            InitialInput = "检查执行日志",
            Provider = "provider",
            Model = "model",
            AgentName = "agent",
        });

        var logService = new WorkExecutionLogService(_db, taskService);
        logService.Append(
            "task-logs",
            WorkExecutionLogTypes.StepStart,
            System.Text.Json.JsonSerializer.Serialize(
                new WorkExecutionLogRecords.StepStart("创建目录", "准备阶段", "mkdir"),
                WorkModeJsonContext.Default.StepStart));
        logService.Append(
            "task-logs",
            WorkExecutionLogTypes.ToolCall,
            System.Text.Json.JsonSerializer.Serialize(
                new WorkExecutionLogRecords.ToolCall("call-1", "sys_write_file", "{\"path\":\"a.txt\"}"),
                WorkModeJsonContext.Default.ToolCall));
        logService.Append(
            "task-logs",
            WorkExecutionLogTypes.ToolResult,
            System.Text.Json.JsonSerializer.Serialize(
                new WorkExecutionLogRecords.ToolResult("call-1", "success", "文件已写入", null),
                WorkModeJsonContext.Default.ToolResult));

        var tools = new ExecutionLogTools(logService, "task-logs");
        var result = await tools.CreateGetExecutionLogsTool().InvokeAsync(new AIFunctionArguments
        {
            ["stepTitle"] = "创建目录",
            ["take"] = 10
        });

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("开始步骤「创建目录」", text);
        Assert.IsFalse(text.Contains("sys_write_file", StringComparison.Ordinal));

        var allResult = await tools.CreateGetExecutionLogsTool().InvokeAsync(new AIFunctionArguments
        {
            ["take"] = 10
        });
        var allText = allResult?.ToString() ?? string.Empty;
        Assert.Contains("调用工具 sys_write_file", allText);
        Assert.Contains("文件已写入", allText);
    }

    private WorkModeToolset CreateToolset(string taskId)
    {
        var taskService = new WorkTaskService(_db);
        var fileService = new WorkTaskFileService(_paths);
        var publisher = _services.GetRequiredService<IPublisher>();
        var projectLeadService = new ProjectLeadService(
            fileService,
            new CompletingDispatcher(),
            publisher,
            NullLogger<ProjectLeadService>.Instance,
            taskService,
            new WorkTaskEventService(_db));

        return new WorkModeToolset(
            taskService,
            new WorkPendingInputService(_db),
            new WorkPlanTemplateService(_db),
            new ChatMessageService(_db),
            fileService,
            new WorkExecutionLogService(_db, taskService),
            projectLeadService,
            NullLogger<ProjectLeadTools>.Instance,
            publisher,
            new WorkTaskCancellationRegistry(),
            titleService: null,
            reportCompactionService: null,
            taskId);
    }

    private static IReadOnlyList<AIFunction> CreateFileTools()
    {
        static string ToolBody() => "ok";

        return
        [
            AIFunctionFactory.Create(name: "sys_list_directory", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_get_file_info", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_read_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_search_files", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_create_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_write_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_write_large_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_write_files_batch", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_edit_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_create_directory", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_delete_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_move_file", method: ToolBody),
            AIFunctionFactory.Create(name: "sys_delete_directory", method: ToolBody)
        ];
    }

    private static IReadOnlyList<string> ExtractAvailableToolNames(string prompt)
    {
        var lines = prompt.Split(["\r\n", "\n"], StringSplitOptions.None);
        var inSection = false;
        var names = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = string.Equals(line.Trim(), "## 可用工具", StringComparison.Ordinal);
                continue;
            }

            if (!inSection || !line.StartsWith("- `", StringComparison.Ordinal))
            {
                continue;
            }

            var start = line.IndexOf('`', StringComparison.Ordinal);
            var end = start >= 0
                ? line.IndexOf('`', start + 1)
                : -1;
            if (start >= 0 && end > start + 1)
            {
                names.Add(line[(start + 1)..end]);
            }
        }

        return names;
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
