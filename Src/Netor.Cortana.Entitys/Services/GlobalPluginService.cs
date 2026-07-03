using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 全局插件配置服务。
/// </summary>
public sealed class GlobalPluginService(CortanaDbContext db)
{
    public List<GlobalPluginEntity> GetAll()
    {
        return db.Query(
            "SELECT * FROM GlobalPlugins ORDER BY CreatedTimestamp DESC",
            ReadEntity);
    }

    public List<string> GetEnabledPluginIds()
    {
        return db.Query(
            "SELECT PluginId FROM GlobalPlugins WHERE IsEnabled = 1",
            static reader => reader.GetString(0));
    }

    public bool IsEnabled(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;

        var value = db.ExecuteScalar<int>(
            "SELECT IsEnabled FROM GlobalPlugins WHERE PluginId = @PluginId",
            command => command.Parameters.AddWithValue("@PluginId", pluginId));

        return value == 1;
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        db.Execute("""
            INSERT INTO GlobalPlugins (Id, CreatedTimestamp, UpdatedTimestamp, PluginId, IsEnabled)
            VALUES (@Id, @Now, @Now, @PluginId, @IsEnabled)
            ON CONFLICT(PluginId) DO UPDATE SET
                UpdatedTimestamp = excluded.UpdatedTimestamp,
                IsEnabled = excluded.IsEnabled
            """,
            command =>
            {
                command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@Now", now);
                command.Parameters.AddWithValue("@PluginId", pluginId);
                command.Parameters.AddWithValue("@IsEnabled", enabled);
            });
    }

    public void Remove(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;

        db.Execute(
            "DELETE FROM GlobalPlugins WHERE PluginId = @PluginId",
            command => command.Parameters.AddWithValue("@PluginId", pluginId));
    }

    private static GlobalPluginEntity ReadEntity(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        CreatedTimestamp = reader.GetInt64(reader.GetOrdinal("CreatedTimestamp")),
        UpdatedTimestamp = reader.GetInt64(reader.GetOrdinal("UpdatedTimestamp")),
        PluginId = reader.GetString(reader.GetOrdinal("PluginId")),
        IsEnabled = reader.GetBoolean(reader.GetOrdinal("IsEnabled"))
    };
}