using Microsoft.Data.Sqlite;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Entitys.Tests.Realtime;

[TestClass]
public sealed class WorkPendingInputServiceTests
{
    private string _root = null!;
    private CortanaDbContext _db = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-work-pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _db.Execute(
            """
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title,
                Summary, RawDiscription, AgentName, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
            VALUES (
                'session-1', 1, 1, 'workspace-1', '待处理输入测试',
                '', '', 'agent-1', 0, 0,
                1, 0, '', 0)
            """);
        _db.Execute(
            """
            INSERT INTO WorkTasks (
                Id, SessionId, WorkspaceId, Title, InitialInput, Provider, Model, AgentName,
                IsActive, CreatedAt, UpdatedAt, LastActiveAt, HasPreemption)
            VALUES (
                'task-1', 'session-1', 'workspace-1', '测试任务', '初始输入', 'provider-1', 'model-1', 'agent-1',
                1, 1, 1, 1, 0)
            """);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Drain_ConsumesAllPendingInputsInOrder()
    {
        var service = new WorkPendingInputService(_db);
        service.Enqueue("task-1", "第一条");
        service.Enqueue("task-1", "第二条");

        var drained = service.Drain("task-1");
        var secondDrain = service.Drain("task-1");

        CollectionAssert.AreEqual(new[] { "第一条", "第二条" }, drained);
        Assert.IsEmpty(secondDrain);
        Assert.IsFalse(service.HasPending("task-1"));
    }
}
