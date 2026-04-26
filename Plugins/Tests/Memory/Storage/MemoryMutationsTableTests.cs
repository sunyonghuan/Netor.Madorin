using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryMutationsTableTests
{
    [TestMethod]
    public void Insert_And_GetById_Should_Save_Mutation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var mutation = MemoryTestData.Mutation("mutation-1");

        fixture.MemoryMutations.Insert(mutation);
        fixture.MemoryMutations.Insert(mutation);

        var saved = fixture.MemoryMutations.GetById("mutation-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(mutation.Id, saved.Id);
        Assert.AreEqual(mutation.MutationType, saved.MutationType);
    }

    [TestMethod]
    public void Upsert_Should_Replace_Mutation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var mutation = MemoryTestData.Mutation("mutation-2");
        fixture.MemoryMutations.Insert(mutation);

        mutation.Reason = "更新后的原因。";
        fixture.MemoryMutations.Upsert(mutation);

        var saved = fixture.MemoryMutations.GetById("mutation-2");

        Assert.IsNotNull(saved);
        Assert.AreEqual("更新后的原因。", saved.Reason);
    }

    [TestMethod]
    public void ListForMemory_Should_Filter_By_Memory()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryMutations.Insert(MemoryTestData.Mutation("mutation-3", "fragment-1"));
        fixture.MemoryMutations.Insert(MemoryTestData.Mutation("mutation-4", "fragment-2"));

        var list = fixture.MemoryMutations.ListForMemory("fragment-1", "fragment", 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("mutation-3", list[0].Id);
    }

    [TestMethod]
    public void Delete_Should_Remove_Mutation()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryMutations.Insert(MemoryTestData.Mutation("mutation-5"));

        Assert.IsTrue(fixture.MemoryMutations.Delete("mutation-5"));
        Assert.IsNull(fixture.MemoryMutations.GetById("mutation-5"));
        Assert.IsFalse(fixture.MemoryMutations.Delete("mutation-5"));
    }
}
