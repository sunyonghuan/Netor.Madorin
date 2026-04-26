using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_mutations 表的数据操作服务，负责记录记忆内容变更审计信息。
/// </summary>
/// <remarks>
/// 该表保存记忆创建、更新、合并、删除等操作前后的 JSON 快照和变更原因，用于排查和追踪记忆演化过程。
/// </remarks>
public sealed class MemoryMutationsTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, memoryId, memoryKind, mutationType, beforeJson, afterJson, reason, traceId, schemaVersion, recordVersion, createdAt";

    /// <summary>
    /// 插入一条记忆变更记录；当记录标识已存在时忽略本次写入。
    /// </summary>
    /// <param name="mutation">需要写入 memory_mutations 表的变更记录。</param>
    public void Insert(MemoryMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO memory_mutations (" + Columns + ") VALUES (@id,@agent,@memoryId,@memoryKind,@mutationType,@before,@after,@reason,@trace,@schema,@record,@created)";
        Bind(command, mutation);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入或替换一条记忆变更记录。
    /// </summary>
    /// <param name="mutation">需要新增或覆盖的变更记录。</param>
    public void Upsert(MemoryMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_mutations (" + Columns + ") VALUES (@id,@agent,@memoryId,@memoryKind,@mutationType,@before,@after,@reason,@trace,@schema,@record,@created)";
        Bind(command, mutation);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据变更记录标识读取单条审计记录。
    /// </summary>
    /// <param name="id">变更记录标识。</param>
    /// <returns>找到的变更记录；不存在时返回 null。</returns>
    public MemoryMutation? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("变更记录标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_mutations WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 查询指定记忆的变更历史。
    /// </summary>
    /// <param name="memoryId">记忆标识。</param>
    /// <param name="memoryKind">记忆类别，例如片段记忆或抽象记忆。</param>
    /// <param name="limit">最多返回的变更记录数量。</param>
    /// <returns>按创建时间倒序排列的变更记录列表。</returns>
    public IReadOnlyList<MemoryMutation> ListForMemory(string memoryId, string memoryKind, int limit)
    {
        if (string.IsNullOrWhiteSpace(memoryId)) throw new ArgumentException("记忆标识不能为空。", nameof(memoryId));
        if (string.IsNullOrWhiteSpace(memoryKind)) throw new ArgumentException("记忆类型不能为空。", nameof(memoryKind));
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_mutations
WHERE memoryId = @memoryId AND memoryKind = @memoryKind
ORDER BY createdAt DESC
LIMIT @limit";
        command.AddParameter("@memoryId", memoryId);
        command.AddParameter("@memoryKind", memoryKind);
        command.AddParameter("@limit", limit);

        var list = new List<MemoryMutation>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 删除指定标识的记忆变更记录。
    /// </summary>
    /// <param name="id">变更记录标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("变更记录标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_mutations WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 将记忆变更记录字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="mutation">参数来源变更记录。</param>
    private static void Bind(SqliteCommand command, MemoryMutation mutation)
    {
        command.AddParameter("@id", mutation.Id);
        command.AddParameter("@agent", mutation.AgentId);
        command.AddParameter("@memoryId", mutation.MemoryId);
        command.AddParameter("@memoryKind", mutation.MemoryKind);
        command.AddParameter("@mutationType", mutation.MutationType);
        command.AddParameter("@before", mutation.BeforeJson);
        command.AddParameter("@after", mutation.AfterJson);
        command.AddParameter("@reason", mutation.Reason);
        command.AddParameter("@trace", mutation.TraceId);
        command.AddParameter("@schema", mutation.SchemaVersion);
        command.AddParameter("@record", mutation.RecordVersion);
        command.AddParameter("@created", mutation.CreatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆变更记录模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆变更记录模型。</returns>
    private static MemoryMutation Read(SqliteDataReader reader)
    {
        return new MemoryMutation
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            MemoryId = reader.GetString(2),
            MemoryKind = reader.GetString(3),
            MutationType = reader.GetString(4),
            BeforeJson = reader.GetNullableString(5),
            AfterJson = reader.GetNullableString(6),
            Reason = reader.GetNullableString(7),
            TraceId = reader.GetNullableString(8),
            SchemaVersion = reader.GetInt32(9),
            RecordVersion = reader.GetInt32(10),
            CreatedAt = reader.GetString(11)
        };
    }
}
