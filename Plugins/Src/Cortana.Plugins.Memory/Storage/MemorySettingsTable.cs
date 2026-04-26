using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_settings 表的数据操作服务，负责记忆系统配置项的存取和有效配置查询。
/// </summary>
/// <remarks>
/// 该表支持全局、工作区、智能体以及智能体加工作区四个层级的配置覆盖，
/// 读取有效配置时会保留更具体配置优先的排序。
/// </remarks>
public sealed class MemorySettingsTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, workspaceId, settingKey, settingValue, valueType, category, description, isEnabled, schemaVersion, recordVersion, createdAt, updatedAt";

    /// <summary>
    /// 插入或替换一条记忆配置。
    /// </summary>
    /// <param name="setting">需要写入 memory_settings 表的配置项。</param>
    public void Upsert(MemorySetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_settings (" + Columns + ") VALUES (@id,@agent,@workspace,@key,@value,@valueType,@category,@description,@enabled,@schema,@record,@created,@updated)";
        Bind(command, setting);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据配置标识读取单条配置。
    /// </summary>
    /// <param name="id">配置标识。</param>
    /// <returns>找到的配置项；不存在时返回 null。</returns>
    public MemorySetting? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("配置标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_settings WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 获取对指定智能体和工作区生效的配置集合。
    /// </summary>
    /// <param name="agentId">可选的智能体标识。</param>
    /// <param name="workspaceId">可选的工作区标识。</param>
    /// <returns>按覆盖优先级从低到高排列的启用配置集合。</returns>
    public IReadOnlyList<MemorySetting> GetEffective(string? agentId, string? workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_settings
WHERE isEnabled = 1
  AND (
    (agentId IS NULL AND workspaceId IS NULL)
    OR (agentId IS NULL AND workspaceId = @workspace)
    OR (agentId = @agent AND workspaceId IS NULL)
    OR (agentId = @agent AND workspaceId = @workspace)
  )
ORDER BY
  CASE WHEN agentId = @agent AND workspaceId = @workspace THEN 3
       WHEN agentId = @agent AND workspaceId IS NULL THEN 2
       WHEN agentId IS NULL AND workspaceId = @workspace THEN 1
       WHEN agentId IS NULL AND workspaceId IS NULL THEN 0
       ELSE 0 END";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);

        return ReadAll(command);
    }

    /// <summary>
    /// 按分类和启用状态筛选记忆配置。
    /// </summary>
    /// <param name="category">可选的配置分类；为 null 时不过滤分类。</param>
    /// <param name="enabledOnly">是否只返回已启用配置。</param>
    /// <param name="limit">最多返回的配置数量。</param>
    /// <returns>按分类和配置键排序的配置列表。</returns>
    public IReadOnlyList<MemorySetting> List(string? category, bool enabledOnly, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_settings
WHERE (@category IS NULL OR category = @category)
  AND (@enabledOnly = 0 OR isEnabled = 1)
ORDER BY category, settingKey
LIMIT @limit";
        command.AddParameter("@category", category);
        command.AddParameter("@enabledOnly", enabledOnly ? 1 : 0);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 删除指定标识的记忆配置。
    /// </summary>
    /// <param name="id">配置标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("配置标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_settings WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 执行命令并把完整结果集映射为记忆配置列表。
    /// </summary>
    /// <param name="command">已经设置好 SQL 和参数的 SQLite 命令。</param>
    /// <returns>记忆配置列表。</returns>
    private static IReadOnlyList<MemorySetting> ReadAll(SqliteCommand command)
    {
        var settings = new List<MemorySetting>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) settings.Add(Read(reader));
        return settings;
    }

    /// <summary>
    /// 将记忆配置字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="setting">参数来源配置项。</param>
    private static void Bind(SqliteCommand command, MemorySetting setting)
    {
        command.AddParameter("@id", setting.Id);
        command.AddParameter("@agent", setting.AgentId);
        command.AddParameter("@workspace", setting.WorkspaceId);
        command.AddParameter("@key", setting.SettingKey);
        command.AddParameter("@value", setting.SettingValue);
        command.AddParameter("@valueType", setting.ValueType);
        command.AddParameter("@category", setting.Category);
        command.AddParameter("@description", setting.Description);
        command.AddParameter("@enabled", setting.IsEnabled ? 1 : 0);
        command.AddParameter("@schema", setting.SchemaVersion);
        command.AddParameter("@record", setting.RecordVersion);
        command.AddParameter("@created", setting.CreatedAt);
        command.AddParameter("@updated", setting.UpdatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆配置模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆配置模型。</returns>
    private static MemorySetting Read(SqliteDataReader reader)
    {
        return new MemorySetting
        {
            Id = reader.GetString(0),
            AgentId = reader.GetNullableString(1),
            WorkspaceId = reader.GetNullableString(2),
            SettingKey = reader.GetString(3),
            SettingValue = reader.GetString(4),
            ValueType = reader.GetString(5),
            Category = reader.GetString(6),
            Description = reader.GetNullableString(7),
            IsEnabled = reader.GetInt32(8) != 0,
            SchemaVersion = reader.GetInt32(9),
            RecordVersion = reader.GetInt32(10),
            CreatedAt = reader.GetString(11),
            UpdatedAt = reader.GetString(12)
        };
    }
}
