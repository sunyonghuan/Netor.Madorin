using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_links 表的数据操作服务，负责维护记忆之间的关系边。
/// </summary>
/// <remarks>
/// 该表用于描述片段记忆、抽象记忆或其他记忆单元之间的支持、冲突、关联等关系，
/// 为召回扩展、解释依据和记忆图谱提供基础数据。
/// </remarks>
public sealed class MemoryLinksTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, sourceMemoryId, sourceMemoryKind, targetMemoryId, targetMemoryKind, relationType, weight, evidenceCount, confidence, schemaVersion, recordVersion, createdAt, updatedAt";

    /// <summary>
    /// 插入或替换一条记忆关系。
    /// </summary>
    /// <param name="link">需要写入 memory_links 表的记忆关系。</param>
    public void Upsert(MemoryLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_links (" + Columns + ") VALUES (@id,@agent,@sourceId,@sourceKind,@targetId,@targetKind,@relation,@weight,@evidence,@confidence,@schema,@record,@created,@updated)";
        Bind(command, link);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据关系标识读取单条记忆关系。
    /// </summary>
    /// <param name="id">记忆关系标识。</param>
    /// <returns>找到的记忆关系；不存在时返回 null。</returns>
    public MemoryLink? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("关系标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_links WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 查询与指定记忆相关的入边和出边关系。
    /// </summary>
    /// <param name="memoryId">记忆标识。</param>
    /// <param name="memoryKind">记忆类别，例如片段记忆或抽象记忆。</param>
    /// <param name="limit">最多返回的关系数量。</param>
    /// <returns>按权重和更新时间排序的记忆关系列表。</returns>
    public IReadOnlyList<MemoryLink> ListByMemory(string memoryId, string memoryKind, int limit)
    {
        if (string.IsNullOrWhiteSpace(memoryId)) throw new ArgumentException("记忆标识不能为空。", nameof(memoryId));
        if (string.IsNullOrWhiteSpace(memoryKind)) throw new ArgumentException("记忆类型不能为空。", nameof(memoryKind));
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_links
WHERE (sourceMemoryId = @memoryId AND sourceMemoryKind = @memoryKind)
   OR (targetMemoryId = @memoryId AND targetMemoryKind = @memoryKind)
ORDER BY weight DESC, updatedAt DESC
LIMIT @limit";
        command.AddParameter("@memoryId", memoryId);
        command.AddParameter("@memoryKind", memoryKind);
        command.AddParameter("@limit", limit);

        var list = new List<MemoryLink>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 删除指定标识的记忆关系。
    /// </summary>
    /// <param name="id">记忆关系标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("关系标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_links WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 将记忆关系字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="link">参数来源记忆关系。</param>
    private static void Bind(SqliteCommand command, MemoryLink link)
    {
        command.AddParameter("@id", link.Id);
        command.AddParameter("@agent", link.AgentId);
        command.AddParameter("@sourceId", link.SourceMemoryId);
        command.AddParameter("@sourceKind", link.SourceMemoryKind);
        command.AddParameter("@targetId", link.TargetMemoryId);
        command.AddParameter("@targetKind", link.TargetMemoryKind);
        command.AddParameter("@relation", link.RelationType);
        command.AddParameter("@weight", link.Weight);
        command.AddParameter("@evidence", link.EvidenceCount);
        command.AddParameter("@confidence", link.Confidence);
        command.AddParameter("@schema", link.SchemaVersion);
        command.AddParameter("@record", link.RecordVersion);
        command.AddParameter("@created", link.CreatedAt);
        command.AddParameter("@updated", link.UpdatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆关系模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆关系模型。</returns>
    private static MemoryLink Read(SqliteDataReader reader)
    {
        return new MemoryLink
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            SourceMemoryId = reader.GetString(2),
            SourceMemoryKind = reader.GetString(3),
            TargetMemoryId = reader.GetString(4),
            TargetMemoryKind = reader.GetString(5),
            RelationType = reader.GetString(6),
            Weight = reader.GetDouble(7),
            EvidenceCount = reader.GetInt32(8),
            Confidence = reader.GetDouble(9),
            SchemaVersion = reader.GetInt32(10),
            RecordVersion = reader.GetInt32(11),
            CreatedAt = reader.GetString(12),
            UpdatedAt = reader.GetString(13)
        };
    }
}
