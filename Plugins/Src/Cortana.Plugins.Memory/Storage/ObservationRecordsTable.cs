using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// observation_records 表的数据操作服务，负责原始观察记录的持久化和读取。
/// </summary>
/// <remarks>
/// 该表保存从对话、事件或外部输入中采集到的原始事实，是后续记忆抽象、整理和召回的输入来源。
/// </remarks>
public sealed class ObservationRecordsTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, workspaceId, sessionId, turnId, messageId, eventType, role, content, attachments, createdTimestamp, modelName, traceId, sourceFacts, schemaVersion, recordVersion, createdAt";

    private const string InsertSql = "INSERT OR IGNORE INTO observation_records (" + Columns + ") VALUES (@id,@agent,@workspace,@sid,@turn,@mid,@etype,@role,@content,@atts,@ts,@model,@trace,@facts,@schema,@record,@created)";

    /// <summary>
    /// 插入一条观察记录；当记录标识已存在时忽略本次写入。
    /// </summary>
    /// <param name="record">需要写入 observation_records 表的观察记录。</param>
    public void Insert(ObservationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        Bind(command, record);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入或替换一条观察记录。
    /// </summary>
    /// <param name="record">需要新增或覆盖的观察记录。</param>
    public void Upsert(ObservationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO observation_records (" + Columns + ") VALUES (@id,@agent,@workspace,@sid,@turn,@mid,@etype,@role,@content,@atts,@ts,@model,@trace,@facts,@schema,@record,@created)";
        Bind(command, record);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 在单个事务中批量插入观察记录；已存在的记录会被忽略。
    /// </summary>
    /// <param name="records">需要批量写入的观察记录集合。</param>
    public void BulkInsert(IEnumerable<ObservationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var list = records as IList<ObservationRecord> ?? records.ToList();
        if (list.Count == 0) return;

        database.ExecuteInTransaction((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertSql;

            foreach (var record in list)
            {
                Bind(command, record);
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// 根据观察记录标识读取单条记录。
    /// </summary>
    /// <param name="id">观察记录标识。</param>
    /// <returns>找到的观察记录；不存在时返回 null。</returns>
    public ObservationRecord? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("观察记录标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM observation_records WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 按智能体和工作区筛选最近的观察记录。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="workspaceId">可选的工作区标识；为 null 时不过滤工作区。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按创建时间倒序排列的观察记录列表。</returns>
    public IReadOnlyList<ObservationRecord> List(string? agentId, string? workspaceId, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM observation_records
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
ORDER BY createdTimestamp DESC, id DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 获取指定处理进度之后尚未处理的观察记录。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="workspaceId">可选的工作区标识；为 null 时不过滤工作区。</param>
    /// <param name="lastObservationTimestamp">上次处理到的观察记录时间戳。</param>
    /// <param name="lastObservationId">上次处理到的观察记录标识，用于同一时间戳内稳定续读。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按创建时间升序排列的待处理观察记录列表。</returns>
    public IReadOnlyList<ObservationRecord> GetUnprocessed(string? agentId, string? workspaceId, long lastObservationTimestamp, string? lastObservationId, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM observation_records
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
  AND (
    createdTimestamp > @lastTimestamp
    OR (createdTimestamp = @lastTimestamp AND (@lastId IS NULL OR id > @lastId))
  )
ORDER BY createdTimestamp, id
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@lastTimestamp", lastObservationTimestamp);
        command.AddParameter("@lastId", lastObservationId);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 删除指定标识的观察记录。
    /// </summary>
    /// <param name="id">观察记录标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("观察记录标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM observation_records WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 执行命令并把完整结果集映射为观察记录列表。
    /// </summary>
    /// <param name="command">已经设置好 SQL 和参数的 SQLite 命令。</param>
    /// <returns>观察记录列表。</returns>
    private static IReadOnlyList<ObservationRecord> ReadAll(SqliteCommand command)
    {
        var records = new List<ObservationRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    /// <summary>
    /// 将观察记录字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="record">参数来源观察记录。</param>
    private static void Bind(SqliteCommand command, ObservationRecord record)
    {
        command.Parameters.Clear();
        command.AddParameter("@id", record.Id);
        command.AddParameter("@agent", record.AgentId);
        command.AddParameter("@workspace", record.WorkspaceId);
        command.AddParameter("@sid", record.SessionId);
        command.AddParameter("@turn", record.TurnId);
        command.AddParameter("@mid", record.MessageId);
        command.AddParameter("@etype", record.EventType);
        command.AddParameter("@role", record.Role);
        command.AddParameter("@content", record.Content);
        command.AddParameter("@atts", record.AttachmentsJson);
        command.AddParameter("@ts", record.CreatedTimestamp);
        command.AddParameter("@model", record.ModelName);
        command.AddParameter("@trace", record.TraceId);
        command.AddParameter("@facts", record.SourceFactsJson);
        command.AddParameter("@schema", record.SchemaVersion);
        command.AddParameter("@record", record.RecordVersion);
        command.AddParameter("@created", record.CreatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为观察记录模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>观察记录模型。</returns>
    private static ObservationRecord Read(SqliteDataReader reader)
    {
        return new ObservationRecord
        {
            Id = reader.GetString(0),
            AgentId = reader.GetNullableString(1),
            WorkspaceId = reader.GetNullableString(2),
            SessionId = reader.GetString(3),
            TurnId = reader.GetNullableString(4),
            MessageId = reader.GetNullableString(5),
            EventType = reader.GetNullableString(6),
            Role = reader.GetString(7),
            Content = reader.GetNullableString(8),
            AttachmentsJson = reader.GetString(9),
            CreatedTimestamp = reader.GetInt64(10),
            ModelName = reader.GetNullableString(11),
            TraceId = reader.GetNullableString(12),
            SourceFactsJson = reader.GetString(13),
            SchemaVersion = reader.GetInt32(14),
            RecordVersion = reader.GetInt32(15),
            CreatedAt = reader.GetString(16)
        };
    }
}
