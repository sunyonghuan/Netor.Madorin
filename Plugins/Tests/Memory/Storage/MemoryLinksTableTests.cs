using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryLinksTableTests
{
    [TestMethod]
    public void Upsert_And_GetById_Should_Save_Link()
    {
        using var fixture = new MemoryStorageTestFixture();
        var link = MemoryTestData.Link("link-1");

        fixture.MemoryLinks.Upsert(link);

        var saved = fixture.MemoryLinks.GetById("link-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(link.Id, saved.Id);
        Assert.AreEqual(link.SourceMemoryId, saved.SourceMemoryId);
        Assert.AreEqual(link.TargetMemoryId, saved.TargetMemoryId);
    }

    [TestMethod]
    public void ListByMemory_Should_Return_Inbound_And_Outbound_Links()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryLinks.Upsert(MemoryTestData.Link("link-2", sourceId: "fragment-1", targetId: "abstraction-1"));
        var inbound = MemoryTestData.Link("link-3", sourceId: "fragment-2", targetId: "fragment-1");
        inbound.TargetMemoryKind = "fragment";
        fixture.MemoryLinks.Upsert(inbound);

        var list = fixture.MemoryLinks.ListByMemory("fragment-1", "fragment", 10);

        Assert.AreEqual(2, list.Count);
        CollectionAssert.AreEquivalent(new[] { "link-2", "link-3" }, list.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public void Delete_Should_Remove_Link()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryLinks.Upsert(MemoryTestData.Link("link-4"));

        Assert.IsTrue(fixture.MemoryLinks.Delete("link-4"));
        Assert.IsNull(fixture.MemoryLinks.GetById("link-4"));
        Assert.IsFalse(fixture.MemoryLinks.Delete("link-4"));
    }
}
