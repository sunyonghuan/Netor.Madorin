using Microsoft.Data.Sqlite;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Entitys.Tests;

[TestClass]
public sealed class AiModelServiceTests
{
    [TestMethod]
    public void GetByProviderId_ExcludesDisabledModels_GetAllIncludesThem()
    {
        using var db = CreateDatabase();
        var service = new AiModelService(db);
        service.Add(CreateModel("enabled", isEnabled: true));
        service.Add(CreateModel("disabled", isEnabled: false));

        Assert.HasCount(1, service.GetByProviderId("provider-1"));
        Assert.HasCount(2, service.GetAllByProviderId("provider-1"));
    }

    [TestMethod]
    public void SetEnabled_PersistsAcrossServiceInstances_AndProtectsDefault()
    {
        var path = CreateDatabasePath();
        using (var db = new CortanaDbContext(path))
        {
            var service = new AiModelService(db);
            service.Add(CreateModel("model-1", isEnabled: true, isDefault: true));
            service.Add(CreateModel("model-2", isEnabled: true));
            service.SetEnabled("model-2", false);

            Assert.ThrowsExactly<InvalidOperationException>(() => service.SetEnabled("model-1", false));
        }

        using var reopenedDb = new CortanaDbContext(path);
        var reopened = new AiModelService(reopenedDb);
        Assert.IsFalse(reopened.GetById("model-2")!.IsEnabled);
        Assert.IsTrue(reopened.GetById("model-1")!.IsEnabled);
    }

    [TestMethod]
    public void SyncByProviderId_PreservesLocalState_AddsNewModels_AndDeletesMissingModels()
    {
        using var db = CreateDatabase();
        var service = new AiModelService(db);
        service.Add(CreateModel("default-id", "default", isEnabled: true, isDefault: true));
        service.Add(CreateModel("disabled-id", "disabled", isEnabled: false));
        service.Add(CreateModel("stale-id", "stale", isEnabled: true));

        var synced = service.SyncByProviderId("provider-1",
        [
            CreateModel(string.Empty, "default", contextLength: 512000),
            CreateModel(string.Empty, "new-model", contextLength: 128000),
            CreateModel(string.Empty, "disabled")
        ]);

        var all = service.GetAllByProviderId("provider-1");
        var preserved = all.Single(model => model.Name == "default");
        var disabled = all.Single(model => model.Name == "disabled");
        var added = all.Single(model => model.Name == "new-model");

        Assert.HasCount(3, synced);
        Assert.HasCount(3, all);
        Assert.AreEqual("default-id", preserved.Id);
        Assert.IsTrue(preserved.IsDefault);
        Assert.AreEqual(512000, preserved.ContextLength);
        Assert.IsFalse(disabled.IsEnabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(added.Id));
        Assert.IsTrue(added.IsEnabled);
        Assert.IsNull(service.GetById("stale-id"));
    }

    [TestMethod]
    public void Delete_DefaultModel_SelectsEnabledModelFromSameProvider()
    {
        using var db = CreateDatabase();
        var service = new AiModelService(db);
        service.Add(CreateModel("default-id", "default", isEnabled: true, isDefault: true));
        service.Add(CreateModel("replacement-id", "replacement", isEnabled: true));
        service.Add(CreateModel("other-id", "other", providerId: "provider-2", isEnabled: true));

        service.Delete("default-id");

        Assert.IsTrue(service.GetById("replacement-id")!.IsDefault);
        Assert.IsFalse(service.GetById("other-id")!.IsDefault);
    }

    [TestMethod]
    public void SyncByProviderId_WhenDefaultDisappears_SelectsReplacementBeforeDeletingIt()
    {
        using var db = CreateDatabase();
        var service = new AiModelService(db);
        service.Add(CreateModel("default-id", "default", isDefault: true));
        service.Add(CreateModel("replacement-id", "replacement"));

        service.SyncByProviderId("provider-1", [CreateModel(string.Empty, "replacement")]);

        Assert.IsNull(service.GetById("default-id"));
        Assert.IsTrue(service.GetById("replacement-id")!.IsDefault);
    }

    [TestMethod]
    public void SetDefault_DisabledModelEnablesIt()
    {
        using var db = CreateDatabase();
        var service = new AiModelService(db);
        service.Add(CreateModel("model-1", "model", isEnabled: false));

        service.SetDefault("model-1");

        var model = service.GetById("model-1")!;
        Assert.IsTrue(model.IsDefault);
        Assert.IsTrue(model.IsEnabled);
    }

    [TestMethod]
    public void ExistingDatabaseWithoutIsEnabled_MigratesModelsAsEnabled()
    {
        var path = CreateDatabasePath();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AiModels (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    DisplayName TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    ContextLength INTEGER NOT NULL DEFAULT 0,
                    ModelType TEXT NOT NULL DEFAULT 'chat',
                    IsDefault INTEGER NOT NULL DEFAULT 0,
                    ProviderId TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO AiModels (Id, CreatedTimestamp, UpdatedTimestamp, Name, ProviderId)
                VALUES ('legacy-id', 1, 1, 'legacy', 'provider-1');
                """;
            command.ExecuteNonQuery();
        }

        using var db = new CortanaDbContext(path);
        var model = new AiModelService(db).GetById("legacy-id");

        Assert.IsNotNull(model);
        Assert.IsTrue(model!.IsEnabled);
    }

    private static CortanaDbContext CreateDatabase()
        => new(CreateDatabasePath());

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cortana-model-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static AiModelEntity CreateModel(
        string id,
        string? name = null,
        string providerId = "provider-1",
        bool isEnabled = true,
        bool isDefault = false,
        int contextLength = 128000)
        => new()
        {
            Id = id,
            Name = name ?? id,
            DisplayName = name ?? id,
            ProviderId = providerId,
            IsEnabled = isEnabled,
            IsDefault = isDefault,
            ContextLength = contextLength,
        };
}
