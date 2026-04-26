using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class ObservationRecordsTableTests
{
    [TestMethod]
    public void Insert_And_GetById_Should_Save_Observation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var record = MemoryTestData.Observation("obs-1");

        fixture.ObservationRecords.Insert(record);
        fixture.ObservationRecords.Insert(record);

        var saved = fixture.ObservationRecords.GetById("obs-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(record.Id, saved.Id);
        Assert.AreEqual(record.AgentId, saved.AgentId);
        Assert.AreEqual(record.WorkspaceId, saved.WorkspaceId);
        Assert.AreEqual(record.Content, saved.Content);
    }

    [TestMethod]
    public void Upsert_Should_Replace_Observation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var record = MemoryTestData.Observation("obs-2");
        fixture.ObservationRecords.Insert(record);

        record.Content = "更新后的观察记录";
        fixture.ObservationRecords.Upsert(record);

        var saved = fixture.ObservationRecords.GetById("obs-2");

        Assert.IsNotNull(saved);
        Assert.AreEqual("更新后的观察记录", saved.Content);
    }

    [TestMethod]
    public void BulkInsert_Should_Insert_Multiple_Observations()
    {
        using var fixture = new MemoryStorageTestFixture();
        var records = new[]
        {
            MemoryTestData.Observation("obs-3", 3000),
            MemoryTestData.Observation("obs-4", 4000)
        };

        fixture.ObservationRecords.BulkInsert(records);

        var list = fixture.ObservationRecords.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(2, list.Count);
        CollectionAssert.AreEquivalent(new[] { "obs-3", "obs-4" }, list.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public void List_Should_Filter_By_Agent_And_Workspace()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.ObservationRecords.BulkInsert([
            MemoryTestData.Observation("obs-5", 5000),
            MemoryTestData.Observation("obs-6", 6000, MemoryTestData.OtherAgentId, MemoryTestData.WorkspaceId),
            MemoryTestData.Observation("obs-7", 7000, MemoryTestData.AgentId, MemoryTestData.OtherWorkspaceId)
        ]);

        var list = fixture.ObservationRecords.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("obs-5", list[0].Id);
    }

    [TestMethod]
    public void GetUnprocessed_Should_Return_Records_After_Last_Position()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.ObservationRecords.BulkInsert([
            MemoryTestData.Observation("obs-08", 8000),
            MemoryTestData.Observation("obs-09", 9000),
            MemoryTestData.Observation("obs-10", 9000)
        ]);

        var list = fixture.ObservationRecords.GetUnprocessed(
            MemoryTestData.AgentId,
            MemoryTestData.WorkspaceId,
            9000,
            "obs-09",
            10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("obs-10", list[0].Id);
    }

    [TestMethod]
    public void Delete_Should_Remove_Observation()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.ObservationRecords.Insert(MemoryTestData.Observation("obs-11"));

        var deleted = fixture.ObservationRecords.Delete("obs-11");
        var missing = fixture.ObservationRecords.GetById("obs-11");

        Assert.IsTrue(deleted);
        Assert.IsNull(missing);
        Assert.IsFalse(fixture.ObservationRecords.Delete("obs-11"));
    }
}
