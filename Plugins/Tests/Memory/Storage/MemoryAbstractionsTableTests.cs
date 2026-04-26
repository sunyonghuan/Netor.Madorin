using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryAbstractionsTableTests
{
    [TestMethod]
    public void Upsert_And_GetById_Should_Save_Abstraction()
    {
        using var fixture = new MemoryStorageTestFixture();
        var abstraction = MemoryTestData.Abstraction("abstraction-1");

        fixture.MemoryAbstractions.Upsert(abstraction);

        var saved = fixture.MemoryAbstractions.GetById("abstraction-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(abstraction.Id, saved.Id);
        Assert.AreEqual(abstraction.Statement, saved.Statement);
        Assert.AreEqual(abstraction.StabilityScore, saved.StabilityScore);
    }

    [TestMethod]
    public void Upsert_Should_Replace_Abstraction()
    {
        using var fixture = new MemoryStorageTestFixture();
        var abstraction = MemoryTestData.Abstraction("abstraction-2");
        fixture.MemoryAbstractions.Upsert(abstraction);

        abstraction.Summary = "更新后的抽象记忆";
        abstraction.Statement = "更新后的抽象记忆";
        fixture.MemoryAbstractions.Upsert(abstraction);

        var saved = fixture.MemoryAbstractions.GetById("abstraction-2");

        Assert.IsNotNull(saved);
        Assert.AreEqual("更新后的抽象记忆", saved.Summary);
    }

    [TestMethod]
    public void List_Should_Filter_By_Agent_And_Workspace()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryAbstractions.Upsert(MemoryTestData.Abstraction("abstraction-3"));
        var other = MemoryTestData.Abstraction("abstraction-4");
        other.WorkspaceId = MemoryTestData.OtherWorkspaceId;
        fixture.MemoryAbstractions.Upsert(other);

        var list = fixture.MemoryAbstractions.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("abstraction-3", list[0].Id);
    }

    [TestMethod]
    public void RecordAccess_Should_Update_Count_And_Time()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryAbstractions.Upsert(MemoryTestData.Abstraction("abstraction-5"));
        var accessedAt = MemoryTestData.ToTime(12000);

        fixture.MemoryAbstractions.RecordAccess("abstraction-5", accessedAt);

        var saved = fixture.MemoryAbstractions.GetById("abstraction-5");

        Assert.IsNotNull(saved);
        Assert.AreEqual(1, saved.AccessCount);
        Assert.AreEqual(accessedAt, saved.LastAccessedAt);
        Assert.AreEqual(accessedAt, saved.UpdatedAt);
    }

    [TestMethod]
    public void Delete_Should_Remove_Abstraction()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryAbstractions.Upsert(MemoryTestData.Abstraction("abstraction-6"));

        Assert.IsTrue(fixture.MemoryAbstractions.Delete("abstraction-6"));
        Assert.IsNull(fixture.MemoryAbstractions.GetById("abstraction-6"));
        Assert.IsFalse(fixture.MemoryAbstractions.Delete("abstraction-6"));
    }
}
