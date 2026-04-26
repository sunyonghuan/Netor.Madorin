using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryStoreTests
{
    [TestMethod]
    public void EnsureInitialized_Should_Create_Default_Settings()
    {
        using var fixture = new MemoryStorageTestFixture();

        var settings = fixture.Store.GetMemorySettings(null, null);

        Assert.IsTrue(settings.Any(static item => item.SettingKey == "recall.maxMemoryCount"));
        Assert.IsTrue(settings.Any(static item => item.SettingKey == "abstraction.enabled"));
    }

    [TestMethod]
    public void Observation_Methods_Should_Insert_And_Read_Unprocessed()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.InsertObservation(MemoryTestData.Observation("store-obs-1", 1000));
        fixture.Store.BulkInsertObservations([
            MemoryTestData.Observation("store-obs-2", 2000),
            MemoryTestData.Observation("store-obs-3", 3000)
        ]);

        var list = fixture.Store.GetUnprocessedObservations("store-processor", MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(3, list.Count);
        CollectionAssert.AreEqual(new[] { "store-obs-1", "store-obs-2", "store-obs-3" }, list.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public void Fragment_And_Abstraction_Methods_Should_Save_And_Search()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.UpsertMemoryFragment(MemoryTestData.Fragment("store-fragment-1", summary: "用户偏好测试"));
        fixture.Store.UpsertMemoryFragment(MemoryTestData.Fragment("store-fragment-2", topic: "abstraction-topic"));
        fixture.Store.UpsertMemoryAbstraction(MemoryTestData.Abstraction("store-abstraction-1", "用户偏好测试抽象"));

        var similar = fixture.Store.SearchSimilarFragments(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, "preference", "用户偏好测试", 10);
        var abstractionCandidates = fixture.Store.GetFragmentsForAbstraction(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, "abstraction-topic", 1, 10);
        var agents = fixture.Store.GetDistinctAgentIds();

        Assert.AreEqual(1, similar.Count);
        Assert.AreEqual("store-fragment-1", similar[0].Id);
        Assert.AreEqual(1, abstractionCandidates.Count);
        Assert.AreEqual("store-fragment-2", abstractionCandidates[0].Id);
        CollectionAssert.Contains(agents.ToArray(), MemoryTestData.AgentId);
    }

    [TestMethod]
    public void SearchRecallCandidates_Should_Return_Fragments_And_Abstractions()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.UpsertMemoryFragment(MemoryTestData.Fragment("store-fragment-3", summary: "用户喜欢 C# 单元测试"));
        fixture.Store.UpsertMemoryAbstraction(MemoryTestData.Abstraction("store-abstraction-2", "用户喜欢 C# 和 .NET"));

        var candidates = fixture.Store.SearchRecallCandidates(
            MemoryTestData.AgentId,
            MemoryTestData.WorkspaceId,
            "C#",
            0.1,
            includeCandidateMemories: true,
            limit: 10);

        Assert.IsTrue(candidates.Any(static item => item.Id == "store-fragment-3" && item.Kind == "fragment"));
        Assert.IsTrue(candidates.Any(static item => item.Id == "store-abstraction-2" && item.Kind == "abstraction"));
    }

    [TestMethod]
    public void RecordMemoryAccesses_Should_Update_Fragment_And_Abstraction()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.UpsertMemoryFragment(MemoryTestData.Fragment("store-fragment-4"));
        fixture.Store.UpsertMemoryAbstraction(MemoryTestData.Abstraction("store-abstraction-3"));
        var accessedAt = MemoryTestData.ToTime(13000);
        var items = new[]
        {
            new MemoryRecallItem { Id = "store-fragment-4", Kind = "fragment" },
            new MemoryRecallItem { Id = "store-abstraction-3", Kind = "abstraction" }
        };

        fixture.Store.RecordMemoryAccesses(items, accessedAt);

        Assert.AreEqual(accessedAt, fixture.MemoryFragments.GetById("store-fragment-4")?.LastAccessedAt);
        Assert.AreEqual(accessedAt, fixture.MemoryAbstractions.GetById("store-abstraction-3")?.LastAccessedAt);
    }

    [TestMethod]
    public void Event_Log_Mutation_Setting_And_State_Methods_Should_Work()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.UpsertMemoryLink(MemoryTestData.Link("store-link-1"));
        fixture.Store.InsertMemoryEvent(MemoryTestData.Event("store-event-1"));
        fixture.Store.InsertRecallLog(MemoryTestData.RecallLog("store-recall-1"));
        fixture.Store.InsertMemoryMutation(MemoryTestData.Mutation("store-mutation-1"));
        fixture.Store.UpsertMemorySetting(MemoryTestData.Setting("store-setting-1", agentId: MemoryTestData.AgentId));
        var state = MemoryTestData.ProcessingState("store-processor-state");
        fixture.Store.UpsertProcessingState(state);

        var settings = fixture.Store.GetMemorySettings(MemoryTestData.AgentId, MemoryTestData.WorkspaceId);
        var savedState = fixture.Store.GetProcessingState("store-processor-state", MemoryTestData.AgentId, MemoryTestData.WorkspaceId);

        Assert.IsNotNull(fixture.MemoryLinks.GetById("store-link-1"));
        Assert.IsNotNull(fixture.MemoryEvents.GetById("store-event-1"));
        Assert.IsNotNull(fixture.RecallLogs.GetById("store-recall-1"));
        Assert.IsNotNull(fixture.MemoryMutations.GetById("store-mutation-1"));
        Assert.IsTrue(settings.Any(static item => item.Id == "store-setting-1"));
        Assert.AreEqual("running", savedState.State);
    }

    [TestMethod]
    public void Store_Should_Throw_Storage_Exception_For_Invalid_Fragment()
    {
        using var fixture = new MemoryStorageTestFixture();
        var invalid = MemoryTestData.Fragment("store-fragment-invalid");
        invalid.AgentId = null!;

        Assert.ThrowsExactly<MemoryStorageException>(() => fixture.Store.UpsertMemoryFragment(invalid));
    }
}
