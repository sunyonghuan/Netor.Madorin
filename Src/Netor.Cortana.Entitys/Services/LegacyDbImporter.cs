using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 旧版 cortana.db 到新版 madorin.db 的一次性配置导入器。
/// 只迁移用户配置表，不迁移 Agent、聊天、会议和任务历史。
/// </summary>
public sealed class LegacyDbImporter(
    CortanaDbContext db,
    SystemSettingsService settings,
    ILogger<LegacyDbImporter> logger)
{
    public const int CurrentSchemaVersion = 2;
    public const string SchemaVersionKey = "Db.SchemaVersion";

    private static readonly string[] TablesToCopy =
    [
        "AiProviders",
        "AiModels",
        "GlobalPlugins",
        "McpServers",
        "Workspaces",
    ];

    /// <summary>
    /// 若当前 madorin.db 尚未完成 v2 初始化，则从同目录 cortana.db 选择性导入配置。
    /// </summary>
    public void EnsureImported(string? legacyPathOverride = null)
    {
        settings.EnsureSetting(
            SchemaVersionKey,
            group: "Db",
            displayName: "数据库结构版本",
            description: "内部迁移控制位，请勿手动修改。",
            defaultValue: "0",
            valueType: "int",
            sortOrder: 0);

        var currentVersion = settings.GetValue(SchemaVersionKey, 0);
        if (currentVersion >= CurrentSchemaVersion)
        {
            return;
        }

        var legacyPath = legacyPathOverride ?? CortanaDbContext.GetLegacyDbPath();
        if (!File.Exists(legacyPath))
        {
            settings.SetValue(SchemaVersionKey, CurrentSchemaVersion.ToString());
            logger.LogInformation("未发现旧版数据库，已将 {Key} 初始化为 {Version}。", SchemaVersionKey, CurrentSchemaVersion);
            return;
        }

        ImportFromLegacy(legacyPath);
    }

    private void ImportFromLegacy(string legacyPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadOnly,
        };

        using var legacy = new SqliteConnection(builder.ToString());
        legacy.Open();

        db.ExecuteInTransaction((target, transaction) =>
        {
            foreach (var table in TablesToCopy)
            {
                CopyTableIfAvailable(legacy, target, transaction, table);
            }

            CopySystemSettings(legacy, target, transaction);
            UpsertSystemSetting(target, transaction, "Agent.DefaultName", "Agent", "默认智能体", "启动新会话或未显式选择智能体时使用的 Agent 名称。", "default", "default", "string", 0);
            UpsertSystemSetting(target, transaction, SchemaVersionKey, "Db", "数据库结构版本", "内部迁移控制位，请勿手动修改。", CurrentSchemaVersion.ToString(), "0", "int", 0);
            return true;
        });

        logger.LogInformation("旧版数据库配置导入完成：{LegacyPath}", legacyPath);
    }

    private static void CopySystemSettings(SqliteConnection legacy, SqliteConnection target, SqliteTransaction transaction)
    {
        if (!TableExists(legacy, "SystemSettings") || !TableExists(target, "SystemSettings"))
        {
            return;
        }

        CopyTable(
            legacy,
            target,
            transaction,
            "SystemSettings",
            "WHERE Id NOT LIKE 'Agent.%' AND Id NOT LIKE 'Db.%'");
    }

    private static void CopyTableIfAvailable(SqliteConnection legacy, SqliteConnection target, SqliteTransaction transaction, string table)
    {
        if (!TableExists(legacy, table) || !TableExists(target, table))
        {
            return;
        }

        CopyTable(legacy, target, transaction, table, whereClause: null);
    }

    private static void CopyTable(
        SqliteConnection legacy,
        SqliteConnection target,
        SqliteTransaction transaction,
        string table,
        string? whereClause)
    {
        var sourceColumns = GetColumnNames(legacy, table);
        var targetColumns = GetColumnNames(target, table);
        var columns = sourceColumns.Where(targetColumns.Contains).ToArray();
        if (columns.Length == 0)
        {
            return;
        }

        using var read = legacy.CreateCommand();
        read.CommandText = $"SELECT {string.Join(", ", columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(table)} {whereClause ?? string.Empty}";

        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            using var write = target.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = $"""
                INSERT OR REPLACE INTO {QuoteIdentifier(table)}
                    ({string.Join(", ", columns.Select(QuoteIdentifier))})
                VALUES
                    ({string.Join(", ", columns.Select(static c => "@" + c))})
                """;

            foreach (var column in columns)
            {
                var value = reader[column];
                write.Parameters.AddWithValue("@" + column, value is DBNull ? DBNull.Value : value);
            }

            write.ExecuteNonQuery();
        }
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @Name";
        command.Parameters.AddWithValue("@Name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void UpsertSystemSetting(
        SqliteConnection target,
        SqliteTransaction transaction,
        string id,
        string group,
        string displayName,
        string description,
        string value,
        string defaultValue,
        string valueType,
        int sortOrder)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO SystemSettings
                (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder)
            VALUES
                (@Id, @Now, @Now, @Group, @DisplayName, @Description, @Value, @DefaultValue, @ValueType, @SortOrder)
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@Group", group);
        command.Parameters.AddWithValue("@DisplayName", displayName);
        command.Parameters.AddWithValue("@Description", description);
        command.Parameters.AddWithValue("@Value", value);
        command.Parameters.AddWithValue("@DefaultValue", defaultValue);
        command.Parameters.AddWithValue("@ValueType", valueType);
        command.Parameters.AddWithValue("@SortOrder", sortOrder);
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
