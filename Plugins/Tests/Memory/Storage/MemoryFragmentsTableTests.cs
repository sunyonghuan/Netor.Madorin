using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemoryFragmentsTableTests
{
    [TestMethod]
    public void Upsert_And_GetById_Should_Save_Fragment()
    {
        using var fixture = new MemoryStorageTestFixture();
        var fragment = MemoryTestData.Fragment("fragment-1");

        fixture.MemoryFragments.Upsert(fragment);

        var saved = fixture.MemoryFragments.GetById("fragment-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(fragment.Id, saved.Id);
        Assert.AreEqual(fragment.Summary, saved.Summary);
        Assert.AreEqual(fragment.Confidence, saved.Confidence);
    }

    [TestMethod]
    public void List_Should_Filter_By_Agent_And_Workspace()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-2"));
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-3", workspaceId: MemoryTestData.OtherWorkspaceId));

        var list = fixture.MemoryFragments.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("fragment-2", list[0].Id);
    }

    [TestMethod]
    public void GetDistinctAgentIds_Should_Return_Saved_Agents()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-4"));
        var other = MemoryTestData.Fragment("fragment-5");
        other.AgentId = MemoryTestData.OtherAgentId;
        fixture.MemoryFragments.Upsert(other);

        var agents = fixture.MemoryFragments.GetDistinctAgentIds();

        CollectionAssert.AreEquivalent(new[] { MemoryTestData.AgentId, MemoryTestData.OtherAgentId }, agents.ToArray());
    }

    [TestMethod]
    public void SearchSimilar_Should_Find_Same_Type_And_Summary()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-6", summary: "用户偏好单元测试"));
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-7", summary: "其他摘要"));

        var list = fixture.MemoryFragments.SearchSimilar(
            MemoryTestData.AgentId,
            MemoryTestData.WorkspaceId,
            "preference",
            "用户偏好单元测试",
            10);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("fragment-6", list[0].Id);
    }

    [TestMethod]
    public void GetForAbstraction_Should_Filter_Topic_And_Order_By_Salience()
    {
        using var fixture = new MemoryStorageTestFixture();
        var lower = MemoryTestData.Fragment("fragment-8", topic: "topic-a");
        lower.SalienceScore = 0.4;
        var higher = MemoryTestData.Fragment("fragment-9", topic: "topic-a");
        higher.SalienceScore = 0.9;
        fixture.MemoryFragments.Upsert(lower);
        fixture.MemoryFragments.Upsert(higher);
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-10", topic: "topic-b"));

        var list = fixture.MemoryFragments.GetForAbstraction(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, "topic-a", 10);

        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("fragment-9", list[0].Id);
        Assert.AreEqual("fragment-8", list[1].Id);
    }

    [TestMethod]
    public void RecordAccess_Should_Update_Count_And_Time()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-11"));
        var accessedAt = MemoryTestData.ToTime(11000);

        fixture.MemoryFragments.RecordAccess("fragment-11", accessedAt);

        var saved = fixture.MemoryFragments.GetById("fragment-11");

        Assert.IsNotNull(saved);
        Assert.AreEqual(1, saved.AccessCount);
        Assert.AreEqual(accessedAt, saved.LastAccessedAt);
        Assert.AreEqual(accessedAt, saved.UpdatedAt);
    }

    [TestMethod]
    public void Delete_Should_Remove_Fragment()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemoryFragments.Upsert(MemoryTestData.Fragment("fragment-12"));

        Assert.IsTrue(fixture.MemoryFragments.Delete("fragment-12"));
        Assert.IsNull(fixture.MemoryFragments.GetById("fragment-12"));
        Assert.IsFalse(fixture.MemoryFragments.Delete("fragment-12"));
    }
}
