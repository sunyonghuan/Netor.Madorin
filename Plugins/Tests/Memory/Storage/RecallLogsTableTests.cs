using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class RecallLogsTableTests
{
    [TestMethod]
    public void Insert_And_GetById_Should_Save_RecallLog()
    {
        using var fixture = new MemoryStorageTestFixture();
        var log = MemoryTestData.RecallLog("recall-1");

        fixture.RecallLogs.Insert(log);
        fixture.RecallLogs.Insert(log);

        var saved = fixture.RecallLogs.GetById("recall-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(log.Id, saved.Id);
        Assert.AreEqual(log.QueryText, saved.QueryText);
    }

    [TestMethod]
    public void Upsert_Should_Replace_RecallLog()
    {
        using var fixture = new MemoryStorageTestFixture();
        var log = MemoryTestData.RecallLog("recall-2");
        fixture.RecallLogs.Insert(log);

        log.RecallSummary = "更新后的召回摘要。";
        fixture.RecallLogs.Upsert(log);

        var saved = fixture.RecallLogs.GetById("recall-2");

        Assert.IsNotNull(saved);
        Assert.AreEqual("更新后的召回摘要。", saved.RecallSummary);
    }

    [TestMethod]
    public void List_Should_Filter_By_Agent_And_Workspace()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.RecallLogs.Insert(MemoryTestData.RecallLog("recall-3"));
        var other = MemoryTestData.RecallLog("recall-4");
        other.WorkspaceId = MemoryTestData.OtherWorkspaceId;
        fixture.RecallLogs.Insert(other);

        var list = fixture.RecallLogs.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("recall-3", list[0].Id);
    }

    [TestMethod]
    public void Delete_Should_Remove_RecallLog()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.RecallLogs.Insert(MemoryTestData.RecallLog("recall-5"));

        Assert.IsTrue(fixture.RecallLogs.Delete("recall-5"));
        Assert.IsNull(fixture.RecallLogs.GetById("recall-5"));
        Assert.IsFalse(fixture.RecallLogs.Delete("recall-5"));
    }
}
