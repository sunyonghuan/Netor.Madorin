using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryEventsTableTests
{
    [TestMethod]
    public void Insert_And_GetById_Should_Save_Event()
    {
        using var fixture = new MemoryStorageTestFixture();
        var memoryEvent = MemoryTestData.Event("event-1");

        fixture.MemoryEvents.Insert(memoryEvent);
        fixture.MemoryEvents.Insert(memoryEvent);

        var saved = fixture.MemoryEvents.GetById("event-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(memoryEvent.EventId, saved.EventId);
        Assert.AreEqual(memoryEvent.EventType, saved.EventType);
    }

    [TestMethod]
    public void Upsert_Should_Replace_Event()
    {
        using var fixture = new MemoryStorageTestFixture();
        var memoryEvent = MemoryTestData.Event("event-2");
        fixture.MemoryEvents.Insert(memoryEvent);

        memoryEvent.PayloadJson = "{\"updated\":true}";
        fixture.MemoryEvents.Upsert(memoryEvent);

        var saved = fixture.MemoryEvents.GetById("event-2");

        Assert.IsNotNull(saved);
        Assert.AreEqual("{\"updated\":true}", saved.PayloadJson);
    }

    [TestMethod]
    public void List_Should_Filter_By_Agent_And_Type()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryEvents.Insert(MemoryTestData.Event("event-3"));
        var other = MemoryTestData.Event("event-4");
        other.EventType = "processing.completed";
        fixture.MemoryEvents.Insert(other);

        var list = fixture.MemoryEvents.List(MemoryTestData.AgentId, "fragment.extracted", 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("event-3", list[0].EventId);
    }

    [TestMethod]
    public void Delete_Should_Remove_Event()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryEvents.Insert(MemoryTestData.Event("event-5"));

        Assert.IsTrue(fixture.MemoryEvents.Delete("event-5"));
        Assert.IsNull(fixture.MemoryEvents.GetById("event-5"));
        Assert.IsFalse(fixture.MemoryEvents.Delete("event-5"));
    }
}
