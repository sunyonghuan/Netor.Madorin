using Microsoft.Data.Sqlite;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Tests.WorkModeFiles;

[TestClass]
public sealed class WorkTaskContextServiceTests
{
    private string _root = null!;
    private CortanaDbContext _db = null!;
    private WorkTaskContextService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-work-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _service = new WorkTaskContextService(_db);
        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                'session-1', 1, 1, 'workspace-1', '上下文测试',
                '', '', 'agent-1', 0, 0,
                1, 0, '', 0)
            """);
        var taskService = new WorkTaskService(_db);
        taskService.Create(new WorkTaskEntity
        {
            Id = "task-1",
            SessionId = "session-1",
            WorkspaceId = "workspace-1",
            Title = "上下文测试",
            InitialInput = "开始",
            Provider = "provider-1",
            Model = "model-1",
            AgentName = "agent-1"
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            DeleteDirectoryWithRetry(_root);
        }
    }

    [TestMethod]
    public void AppendMessage_IsolatesMessagesByRunId()
    {
        _service.AppendMessage("task-1", "run-a", "user", "A 用户消息");
        _service.AppendMessage("task-1", "run-a", "assistant", "A 回复");
        _service.AppendMessage("task-1", "run-b", "user", "B 用户消息");

        var runA = _service.ListMessages("task-1", "run-a");
        var runB = _service.ListMessages("task-1", "run-b");

        Assert.HasCount(2, runA);
        Assert.AreEqual(1, runA[0].Sequence);
        Assert.AreEqual(2, runA[1].Sequence);
        Assert.AreEqual("A 用户消息", runA[0].Content);
        Assert.HasCount(1, runB);
        Assert.AreEqual(1, runB[0].Sequence);
        Assert.AreEqual("B 用户消息", runB[0].Content);
    }

    [TestMethod]
    public void AddSegment_IsolatesSegmentsByRunId()
    {
        _service.AddSegment(new WorkTaskContextSegmentEntity
        {
            TaskId = "task-1",
            RunId = "run-a",
            SegmentIndex = 0,
            StartSequence = 1,
            EndSequence = 3,
            Summary = "A 摘要",
            OriginalMessageCount = 3,
            ModelName = "test-model"
        });
        _service.AddSegment(new WorkTaskContextSegmentEntity
        {
            TaskId = "task-1",
            RunId = "run-b",
            SegmentIndex = 0,
            StartSequence = 1,
            EndSequence = 1,
            Summary = "B 摘要",
            OriginalMessageCount = 1,
            ModelName = "test-model"
        });

        var runA = _service.ListSegments("task-1", "run-a");
        var runB = _service.ListSegments("task-1", "run-b");

        Assert.HasCount(1, runA);
        Assert.AreEqual("A 摘要", runA[0].Summary);
        Assert.HasCount(1, runB);
        Assert.AreEqual("B 摘要", runB[0].Summary);
    }

    [TestMethod]
    public void DeleteByTask_RemovesMessagesAndSegments()
    {
        _service.AppendMessage("task-1", "run-a", "user", "消息");
        _service.AddSegment(new WorkTaskContextSegmentEntity
        {
            TaskId = "task-1",
            RunId = "run-a",
            SegmentIndex = 0,
            Summary = "摘要"
        });

        var deleted = _service.DeleteByTask("task-1");

        Assert.AreEqual(2, deleted);
        Assert.IsEmpty(_service.ListMessages("task-1", "run-a"));
        Assert.IsEmpty(_service.ListSegments("task-1", "run-a"));
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
}
