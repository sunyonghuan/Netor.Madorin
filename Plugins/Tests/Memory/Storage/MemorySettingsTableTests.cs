using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Storage;

[TestClass]
public sealed class MemorySettingsTableTests
{
    [TestMethod]
    public void Upsert_And_GetById_Should_Save_Setting()
    {
        using var fixture = new MemoryStorageTestFixture();
        var setting = MemoryTestData.Setting("setting-1");

        fixture.MemorySettings.Upsert(setting);

        var saved = fixture.MemorySettings.GetById("setting-1");

        Assert.IsNotNull(saved);
        Assert.AreEqual(setting.Id, saved.Id);
        Assert.AreEqual(setting.SettingKey, saved.SettingKey);
        Assert.AreEqual(setting.SettingValue, saved.SettingValue);
    }

    [TestMethod]
    public void GetEffective_Should_Return_Enabled_Settings_In_Override_Order()
    {
        using var fixture = new MemoryStorageTestFixture();
        const string key = "test.effective.order";
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-2", key, "1"));
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-3", key, "2", workspaceId: MemoryTestData.WorkspaceId));
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-4", key, "3", agentId: MemoryTestData.AgentId));
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-5", key, "4", agentId: MemoryTestData.AgentId, workspaceId: MemoryTestData.WorkspaceId));
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-6", key, "5", agentId: MemoryTestData.AgentId, workspaceId: MemoryTestData.WorkspaceId, enabled: false));

        var effective = fixture.MemorySettings.GetEffective(MemoryTestData.AgentId, MemoryTestData.WorkspaceId)
            .Where(static setting => setting.SettingKey == key)
            .ToList();

        CollectionAssert.AreEqual(new[] { "setting-2", "setting-3", "setting-4" }, effective.Select(static item => item.Id).ToArray());
        Assert.IsFalse(effective.Any(static item => item.Id == "setting-6"));
    }

    [TestMethod]
    public void List_Should_Filter_Category_And_Enabled_State()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-7", enabled: true));
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-8", enabled: false));
        var otherCategory = MemoryTestData.Setting("setting-9");
        otherCategory.Category = "decay";
        fixture.MemorySettings.Upsert(otherCategory);

        var list = fixture.MemorySettings.List("recall", enabledOnly: true, limit: 10)
            .Where(static setting => setting.Id.StartsWith("setting-", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("setting-7", list[0].Id);
    }

    [TestMethod]
    public void Delete_Should_Remove_Setting()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.MemorySettings.Upsert(MemoryTestData.Setting("setting-10"));

        Assert.IsTrue(fixture.MemorySettings.Delete("setting-10"));
        Assert.IsNull(fixture.MemorySettings.GetById("setting-10"));
        Assert.IsFalse(fixture.MemorySettings.Delete("setting-10"));
    }
}
