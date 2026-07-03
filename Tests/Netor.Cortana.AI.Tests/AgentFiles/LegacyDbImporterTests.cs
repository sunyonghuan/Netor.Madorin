using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Tests.AgentFiles;

[TestClass]
public sealed class LegacyDbImporterTests
{
    [TestMethod]
    public void EnsureImported_CopiesConfigurationTablesAndAdvancesSchemaVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cortana-legacy-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "cortana.db");
        var targetPath = Path.Combine(directory, "madorin.db");

        try
        {
            CreateLegacyDatabase(legacyPath);

            using (var db = new CortanaDbContext(targetPath))
            {
                var settings = new SystemSettingsService(db);
                settings.EnsureSetting("Db.SchemaVersion", "Db", "数据库结构版本", "内部迁移控制位，请勿手动修改。", "0", "int", 0);
                settings.EnsureSetting("Compaction.SegmentSize", "对话历史", "压缩段落大小", "", "30", "int", 1);

                var importer = new LegacyDbImporter(db, settings, NullLogger<LegacyDbImporter>.Instance);
                importer.EnsureImported(legacyPath);

                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM AiProviders WHERE Id = 'provider-old'"));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM AiModels WHERE Id = 'model-old'"));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM GlobalPlugins WHERE PluginId = 'plugin.old'"));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM McpServers WHERE Id = 'mcp-old'"));
                Assert.AreEqual("128", settings.GetValue("Compaction.SegmentSize"));
                Assert.AreEqual("default", settings.GetValue("Agent.DefaultName"));
                Assert.AreEqual("2", settings.GetValue("Db.SchemaVersion"));
                Assert.IsNull(settings.GetValue("Agent.LegacyDefault"));
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(directory);
        }
    }

    private static void CreateLegacyDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, """
            CREATE TABLE AiProviders (
                Id TEXT PRIMARY KEY,
                CreatedTimestamp INTEGER NOT NULL,
                UpdatedTimestamp INTEGER NOT NULL,
                Name TEXT NOT NULL DEFAULT '',
                Url TEXT NOT NULL DEFAULT '',
                Key TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                ProviderType TEXT NOT NULL DEFAULT 'OpenAI',
                IsDefault INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute(connection, """
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
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                ProviderId TEXT NOT NULL DEFAULT ''
            );
            """);
        Execute(connection, """
            CREATE TABLE GlobalPlugins (
                Id TEXT PRIMARY KEY,
                CreatedTimestamp INTEGER NOT NULL,
                UpdatedTimestamp INTEGER NOT NULL,
                PluginId TEXT NOT NULL UNIQUE,
                IsEnabled INTEGER NOT NULL DEFAULT 1
            );
            """);
        Execute(connection, """
            CREATE TABLE McpServers (
                Id TEXT PRIMARY KEY,
                CreatedTimestamp INTEGER NOT NULL,
                UpdatedTimestamp INTEGER NOT NULL,
                Name TEXT NOT NULL DEFAULT '',
                TransportType TEXT NOT NULL DEFAULT 'stdio',
                Command TEXT NOT NULL DEFAULT '',
                Arguments TEXT NOT NULL DEFAULT '[]',
                Url TEXT NOT NULL DEFAULT '',
                ApiKey TEXT NOT NULL DEFAULT '',
                EnvironmentVariables TEXT NOT NULL DEFAULT '{}',
                Description TEXT NOT NULL DEFAULT '',
                IsEnabled INTEGER NOT NULL DEFAULT 1
            );
            """);
        Execute(connection, """
            CREATE TABLE SystemSettings (
                Id TEXT PRIMARY KEY,
                CreatedTimestamp INTEGER NOT NULL,
                UpdatedTimestamp INTEGER NOT NULL,
                [Group] TEXT NOT NULL DEFAULT '',
                DisplayName TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                Value TEXT NOT NULL DEFAULT '',
                DefaultValue TEXT NOT NULL DEFAULT '',
                ValueType TEXT NOT NULL DEFAULT 'string',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute(connection, "INSERT INTO AiProviders (Id, CreatedTimestamp, UpdatedTimestamp, Name, Url, Key, Description, ProviderType, IsDefault, IsEnabled, SortOrder) VALUES ('provider-old', 1, 1, '旧厂商', 'https://example.test', 'key', '', 'OpenAI', 1, 1, 0)");
        Execute(connection, "INSERT INTO AiModels (Id, CreatedTimestamp, UpdatedTimestamp, Name, DisplayName, Description, ContextLength, ModelType, IsDefault, IsEnabled, ProviderId) VALUES ('model-old', 1, 1, 'model', '模型', '', 8192, 'chat', 1, 1, 'provider-old')");
        Execute(connection, "INSERT INTO GlobalPlugins (Id, CreatedTimestamp, UpdatedTimestamp, PluginId, IsEnabled) VALUES ('global-plugin-old', 1, 1, 'plugin.old', 1)");
        Execute(connection, "INSERT INTO McpServers (Id, CreatedTimestamp, UpdatedTimestamp, Name, TransportType, Command, Arguments, Url, ApiKey, EnvironmentVariables, Description, IsEnabled) VALUES ('mcp-old', 1, 1, '旧 MCP', 'stdio', 'node', '[]', '', '', '{}', '', 1)");
        Execute(connection, "INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder) VALUES ('Compaction.SegmentSize', 1, 1, '对话历史', '压缩段落大小', '', '128', '30', 'int', 1)");
        Execute(connection, "INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder) VALUES ('Agent.LegacyDefault', 1, 1, 'Agent', '旧默认', '', 'legacy', 'legacy', 'string', 0)");
        Execute(connection, "INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder) VALUES ('Db.SchemaVersion', 1, 1, 'Db', '旧版本', '', '1', '0', 'int', 0)");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
