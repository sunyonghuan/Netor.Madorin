using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class PluginBusWorkflowHistoryDispatcherTests
{
    private string _dbPath = null!;
    private string _root = null!;
    private CortanaDbContext _db = null!;
    private SystemSettingsService _settings = null!;
    private WorkTaskService _tasks = null!;
    private List<string> _sent = null!;
    private PluginBusWorkflowHistoryDispatcher _dispatcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-workflow-replay-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"cortana-workflow-replay-agents-{Guid.NewGuid():N}");
        _db = new CortanaDbContext(_dbPath);
        _settings = new SystemSettingsService(_db);
        _tasks = new WorkTaskService(_db);
        _sent = [];
        _dispatcher = new PluginBusWorkflowHistoryDispatcher(
            _db,
            CreateAgentService(),
            NullLogger.Instance,
            (clientId, message, cancellationToken) =>
            {
                _sent.Add(message);
                return Task.CompletedTask;
            });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPath);
        DeleteIfExists($"{_dbPath}-shm");
        DeleteIfExists($"{_dbPath}-wal");
        DeleteDirectoryIfExists(_root);
    }

    [TestMethod]
    public async Task ReplayAsync_DoesNotSkipTasksWithSameLastActiveAtAcrossBatches()
    {
        const long timestamp = 1_700_000_000_000;
        var firstTaskId = CreateCompletedTask("task-a", "agent-off", "第一份报告");
        var secondTaskId = CreateCompletedTask("task-b", "agent-on", "第二份报告");
        ForceTaskLastActiveAt(firstTaskId, timestamp);
        ForceTaskLastActiveAt(secondTaskId, timestamp);

        await _dispatcher.ReplayAsync("client-1", "request-same-ms", 0, 1, CancellationToken.None);

        Assert.HasCount(3, _sent);
        using var firstBatchDoc = JsonDocument.Parse(_sent[0]);
        using var secondBatchDoc = JsonDocument.Parse(_sent[1]);
        using var completedDoc = JsonDocument.Parse(_sent[2]);
        var firstItems = firstBatchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();
        var secondItems = secondBatchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, firstItems);
        Assert.HasCount(1, secondItems);
        var items = firstItems.Concat(secondItems).ToArray();
        var exportedIds = items.Select(item => item.GetProperty("taskId").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(exportedIds.Contains(firstTaskId));
        Assert.IsTrue(exportedIds.Contains(secondTaskId));
        Assert.AreEqual(2, completedDoc.RootElement.GetProperty("payload").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task ReplayAsync_ExportsAgentDisplayNameAndMemorySwitch()
    {
        var taskId = CreateCompletedTask("task-memory-off", "agent-off", "关闭记忆报告");

        await _dispatcher.ReplayAsync("client-1", "request-memory-off", 0, 100, CancellationToken.None);

        Assert.HasCount(2, _sent);
        using var batchDoc = JsonDocument.Parse(_sent[0]);
        var items = batchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, items);
        Assert.AreEqual(taskId, items[0].GetProperty("taskId").GetString());
        Assert.AreEqual("agent-off", items[0].GetProperty("managerAgentId").GetString());
        Assert.AreEqual("关闭记忆 Agent", items[0].GetProperty("managerAgentName").GetString());
        Assert.IsFalse(items[0].GetProperty("allowMemoryIngest").GetBoolean());
    }

    [TestMethod]
    public async Task ReplayAsync_SkipsFailedAndEmptyReportTasks()
    {
        var completedTaskId = CreateCompletedTask("task-completed", "agent-on", "可入库报告");
        CreateFailedTask("task-failed", "agent-on");
        CreateCompletedTask("task-empty-report", "agent-on", string.Empty);

        await _dispatcher.ReplayAsync("client-1", "request-filter", 0, 100, CancellationToken.None);

        Assert.HasCount(2, _sent);
        using var batchDoc = JsonDocument.Parse(_sent[0]);
        using var completedDoc = JsonDocument.Parse(_sent[1]);
        var items = batchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, items);
        Assert.AreEqual(completedTaskId, items[0].GetProperty("taskId").GetString());
        Assert.AreEqual("可入库报告", items[0].GetProperty("finalReport").GetString());
        Assert.AreEqual(1, completedDoc.RootElement.GetProperty("payload").GetProperty("total").GetInt32());
    }

    private string CreateCompletedTask(string taskId, string agentName, string finalReport)
    {
        var sessionId = CreateChatSession($"workspace-{taskId}", agentName);
        _tasks.Create(new WorkTaskEntity
        {
            Id = taskId,
            SessionId = sessionId,
            WorkspaceId = $"workspace-{taskId}",
            Title = $"任务 {taskId}",
            InitialInput = $"输入 {taskId}",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = agentName,
        });
        _tasks.RecordExecutionCompleted(taskId, finalReport);
        return taskId;
    }

    private string CreateFailedTask(string taskId, string agentName)
    {
        var sessionId = CreateChatSession($"workspace-{taskId}", agentName);
        _tasks.Create(new WorkTaskEntity
        {
            Id = taskId,
            SessionId = sessionId,
            WorkspaceId = $"workspace-{taskId}",
            Title = $"任务 {taskId}",
            InitialInput = $"输入 {taskId}",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = agentName,
        });
        _tasks.RecordExecutionFailed(taskId, "执行失败");
        return taskId;
    }

    private string CreateChatSession(string workspaceId, string agentName)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var sessionId = Guid.NewGuid().ToString("N");
        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary,
                RawDiscription, AgentName, IsArchived, IsPinned, LastActiveTimestamp,
                TotalTokenCount, CompactedContext, CompactedAtCount
            ) VALUES (
                @Id, @Now, @Now, @WorkspaceId, @Title, '', '', @AgentName, 0, 0, @Now,
                0, '', 0
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", sessionId);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Title", $"会话 {workspaceId}");
                cmd.Parameters.AddWithValue("@AgentName", agentName);
            });

        return sessionId;
    }

    private void ForceTaskLastActiveAt(string taskId, long timestamp)
    {
        _db.Execute(
            "UPDATE WorkTasks SET LastActiveAt = @Timestamp, CompletedAt = @Timestamp WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Timestamp", timestamp);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    private AgentService CreateAgentService()
    {
        var paths = new TestAppPaths(_root);
        var serializer = new AgentManifestSerializer();
        var validator = new AgentManifestValidator();
        var index = new AgentFileIndex(serializer, validator, NullLogger<AgentFileIndex>.Instance);
        Directory.CreateDirectory(paths.UserAgentsDirectory);
        var fileService = new AgentFileService(paths, serializer, validator, index, NullLogger<AgentFileService>.Instance);
        fileService.Save(
            new AgentManifest
            {
                Name = "agent-off",
                DisplayName = "关闭记忆 Agent",
                Description = "用于测试关闭 workflow 记忆。",
                Kind = AgentManifestKinds.Agent,
                Enabled = true,
                AllowWorkflowMemory = false,
            },
            "你是关闭记忆测试 Agent。");
        fileService.Save(
            new AgentManifest
            {
                Name = "agent-on",
                DisplayName = "开启记忆 Agent",
                Description = "用于测试开启 workflow 记忆。",
                Kind = AgentManifestKinds.Agent,
                Enabled = true,
                AllowWorkflowMemory = true,
            },
            "你是开启记忆测试 Agent。");
        return new AgentService(fileService, _settings);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
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
