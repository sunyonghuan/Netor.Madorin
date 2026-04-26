using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_events 表的数据操作服务，负责记录记忆系统内部事件。
/// </summary>
/// <remarks>
/// 该表保存记忆写入、整理、抽象、召回等流程产生的事件载荷，便于后续诊断、审计或异步处理。
/// </remarks>
public sealed class MemoryEventsTable(IMemoryDatabase database)
{
    private const string Columns = "eventId, agentId, eventType, payloadJson, processedAt, schemaVersion, recordVersion";

    /// <summary>
    /// 插入一条记忆事件；当事件标识已存在时忽略本次写入。
    /// </summary>
    /// <param name="memoryEvent">需要写入 memory_events 表的记忆事件。</param>
    public void Insert(MemoryEvent memoryEvent)
    {
        ArgumentNullException.ThrowIfNull(memoryEvent);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO memory_events (" + Columns + ") VALUES (@eventId,@agent,@eventType,@payload,@processed,@schema,@record)";
        Bind(command, memoryEvent);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入或替换一条记忆事件。
    /// </summary>
    /// <param name="memoryEvent">需要新增或覆盖的记忆事件。</param>
    public void Upsert(MemoryEvent memoryEvent)
    {
        ArgumentNullException.ThrowIfNull(memoryEvent);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_events (" + Columns + ") VALUES (@eventId,@agent,@eventType,@payload,@processed,@schema,@record)";
        Bind(command, memoryEvent);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据事件标识读取单条记忆事件。
    /// </summary>
    /// <param name="eventId">记忆事件标识。</param>
    /// <returns>找到的记忆事件；不存在时返回 null。</returns>
    public MemoryEvent? GetById(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("事件标识不能为空。", nameof(eventId));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_events WHERE eventId = @eventId LIMIT 1";
        command.AddParameter("@eventId", eventId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 按智能体和事件类型筛选记忆事件。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="eventType">可选的事件类型；为 null 时不过滤类型。</param>
    /// <param name="limit">最多返回的事件数量。</param>
    /// <returns>按事件标识倒序排列的记忆事件列表。</returns>
    public IReadOnlyList<MemoryEvent> List(string? agentId, string? eventType, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_events
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@eventType IS NULL OR eventType = @eventType)
ORDER BY eventId DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@eventType", eventType);
        command.AddParameter("@limit", limit);

        var list = new List<MemoryEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 删除指定标识的记忆事件。
    /// </summary>
    /// <param name="eventId">记忆事件标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("事件标识不能为空。", nameof(eventId));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_events WHERE eventId = @eventId";
        command.AddParameter("@eventId", eventId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 将记忆事件字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="memoryEvent">参数来源记忆事件。</param>
    private static void Bind(SqliteCommand command, MemoryEvent memoryEvent)
    {
        command.AddParameter("@eventId", memoryEvent.EventId);
        command.AddParameter("@agent", memoryEvent.AgentId);
        command.AddParameter("@eventType", memoryEvent.EventType);
        command.AddParameter("@payload", memoryEvent.PayloadJson);
        command.AddParameter("@processed", memoryEvent.ProcessedAt);
        command.AddParameter("@schema", memoryEvent.SchemaVersion);
        command.AddParameter("@record", memoryEvent.RecordVersion);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆事件模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆事件模型。</returns>
    private static MemoryEvent Read(SqliteDataReader reader)
    {
        return new MemoryEvent
        {
            EventId = reader.GetString(0),
            AgentId = reader.GetString(1),
            EventType = reader.GetString(2),
            PayloadJson = reader.GetString(3),
            ProcessedAt = reader.GetNullableString(4),
            SchemaVersion = reader.GetInt32(5),
            RecordVersion = reader.GetInt32(6)
        };
    }
}
