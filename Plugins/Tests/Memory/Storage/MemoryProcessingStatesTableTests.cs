using Cortana.Plugins.Memory.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryProcessingStatesTableTests
{
    [TestMethod]
    public void Get_Should_Return_Default_Idle_State_When_Not_Exists()
    {
        using var fixture = new MemoryStorageTestFixture();

        var state = fixture.ProcessingStates.Get("processor-default", MemoryTestData.AgentId, MemoryTestData.WorkspaceId);

        Assert.AreEqual("processor-default", state.ProcessorName);
        Assert.AreEqual(MemoryTestData.AgentId, state.AgentId);
        Assert.AreEqual(MemoryTestData.WorkspaceId, state.WorkspaceId);
        Assert.AreEqual("idle", state.State);
        Assert.AreEqual("processor-default:agent-test:workspace-test", state.Id);
    }

    [TestMethod]
    public void Upsert_And_Get_Should_Save_State()
    {
        using var fixture = new MemoryStorageTestFixture();
        var state = MemoryTestData.ProcessingState("processor-1");

        fixture.ProcessingStates.Upsert(state);

        var saved = fixture.ProcessingStates.Get("processor-1", MemoryTestData.AgentId, MemoryTestData.WorkspaceId);

        Assert.AreEqual("running", saved.State);
        Assert.AreEqual(3, saved.ProcessedCount);
        Assert.AreEqual("obs-1", saved.LastObservationId);
    }

    [TestMethod]
    public void Upsert_Should_Create_Id_When_Missing()
    {
        using var fixture = new MemoryStorageTestFixture();
        var state = MemoryTestData.ProcessingState("processor-2");
        state.Id = string.Empty;

        fixture.ProcessingStates.Upsert(state);

        Assert.AreEqual(MemoryProcessingStatesTable.CreateId("processor-2", MemoryTestData.AgentId, MemoryTestData.WorkspaceId), state.Id);
        Assert.IsNotNull(fixture.ProcessingStates.Get("processor-2", MemoryTestData.AgentId, MemoryTestData.WorkspaceId));
    }

    [TestMethod]
    public void List_Should_Filter_By_Processor()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.ProcessingStates.Upsert(MemoryTestData.ProcessingState("processor-3"));
        fixture.ProcessingStates.Upsert(MemoryTestData.ProcessingState("processor-4"));

        var list = fixture.ProcessingStates.List("processor-3", 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("processor-3", list[0].ProcessorName);
    }

    [TestMethod]
    public void Delete_Should_Remove_State()
    {
        using var fixture = new MemoryStorageTestFixture();
        var state = MemoryTestData.ProcessingState("processor-5");
        fixture.ProcessingStates.Upsert(state);

        Assert.IsTrue(fixture.ProcessingStates.Delete(state.Id));
        Assert.AreEqual("idle", fixture.ProcessingStates.Get("processor-5", MemoryTestData.AgentId, MemoryTestData.WorkspaceId).State);
        Assert.IsFalse(fixture.ProcessingStates.Delete(state.Id));
    }
}
