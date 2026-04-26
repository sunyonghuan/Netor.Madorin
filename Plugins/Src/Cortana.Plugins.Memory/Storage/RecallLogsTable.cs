using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// recall_logs 表的数据操作服务，负责记录记忆召回请求和命中结果。
/// </summary>
/// <remarks>
/// 该表用于审计每次召回的查询、命中记忆、抑制记忆、预算策略和置信度，方便后续评估召回质量。
/// </remarks>
public sealed class RecallLogsTable(IMemoryDatabase database)
{
    private const string Columns = "id, requestId, agentId, workspaceId, queryText, queryIntent, triggerSource, hitMemoryIdsJson, supportingMemoryIdsJson, suppressedMemoryIdsJson, recallSummary, confidence, budgetJson, appliedPolicyJson, traceId, schemaVersion, recordVersion, createdAt";

    /// <summary>
    /// 插入一条召回日志；当日志标识已存在时忽略本次写入。
    /// </summary>
    /// <param name="recallLog">需要写入 recall_logs 表的召回日志。</param>
    public void Insert(RecallLog recallLog)
    {
        ArgumentNullException.ThrowIfNull(recallLog);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO recall_logs (" + Columns + ") VALUES (@id,@request,@agent,@workspace,@query,@intent,@trigger,@hits,@supporting,@suppressed,@summary,@confidence,@budget,@policy,@trace,@schema,@record,@created)";
        Bind(command, recallLog);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 插入或替换一条召回日志。
    /// </summary>
    /// <param name="recallLog">需要新增或覆盖的召回日志。</param>
    public void Upsert(RecallLog recallLog)
    {
        ArgumentNullException.ThrowIfNull(recallLog);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO recall_logs (" + Columns + ") VALUES (@id,@request,@agent,@workspace,@query,@intent,@trigger,@hits,@supporting,@suppressed,@summary,@confidence,@budget,@policy,@trace,@schema,@record,@created)";
        Bind(command, recallLog);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据召回日志标识读取单条日志。
    /// </summary>
    /// <param name="id">召回日志标识。</param>
    /// <returns>找到的召回日志；不存在时返回 null。</returns>
    public RecallLog? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("召回日志标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM recall_logs WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 按智能体和工作区筛选最近的召回日志。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="workspaceId">可选的工作区标识；为 null 时不过滤工作区。</param>
    /// <param name="limit">最多返回的日志数量。</param>
    /// <returns>按创建时间倒序排列的召回日志列表。</returns>
    public IReadOnlyList<RecallLog> List(string? agentId, string? workspaceId, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM recall_logs
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
ORDER BY createdAt DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@limit", limit);

        var list = new List<RecallLog>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 删除指定标识的召回日志。
    /// </summary>
    /// <param name="id">召回日志标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("召回日志标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recall_logs WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 将召回日志字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="recallLog">参数来源召回日志。</param>
    private static void Bind(SqliteCommand command, RecallLog recallLog)
    {
        command.AddParameter("@id", recallLog.Id);
        command.AddParameter("@request", recallLog.RequestId);
        command.AddParameter("@agent", recallLog.AgentId);
        command.AddParameter("@workspace", recallLog.WorkspaceId);
        command.AddParameter("@query", recallLog.QueryText);
        command.AddParameter("@intent", recallLog.QueryIntent);
        command.AddParameter("@trigger", recallLog.TriggerSource);
        command.AddParameter("@hits", recallLog.HitMemoryIdsJson);
        command.AddParameter("@supporting", recallLog.SupportingMemoryIdsJson);
        command.AddParameter("@suppressed", recallLog.SuppressedMemoryIdsJson);
        command.AddParameter("@summary", recallLog.RecallSummary);
        command.AddParameter("@confidence", recallLog.Confidence);
        command.AddParameter("@budget", recallLog.BudgetJson);
        command.AddParameter("@policy", recallLog.AppliedPolicyJson);
        command.AddParameter("@trace", recallLog.TraceId);
        command.AddParameter("@schema", recallLog.SchemaVersion);
        command.AddParameter("@record", recallLog.RecordVersion);
        command.AddParameter("@created", recallLog.CreatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为召回日志模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>召回日志模型。</returns>
    private static RecallLog Read(SqliteDataReader reader)
    {
        return new RecallLog
        {
            Id = reader.GetString(0),
            RequestId = reader.GetString(1),
            AgentId = reader.GetString(2),
            WorkspaceId = reader.GetNullableString(3),
            QueryText = reader.GetNullableString(4),
            QueryIntent = reader.GetNullableString(5),
            TriggerSource = reader.GetNullableString(6),
            HitMemoryIdsJson = reader.GetString(7),
            SupportingMemoryIdsJson = reader.GetString(8),
            SuppressedMemoryIdsJson = reader.GetString(9),
            RecallSummary = reader.GetNullableString(10),
            Confidence = reader.GetDouble(11),
            BudgetJson = reader.GetNullableString(12),
            AppliedPolicyJson = reader.GetNullableString(13),
            TraceId = reader.GetNullableString(14),
            SchemaVersion = reader.GetInt32(15),
            RecordVersion = reader.GetInt32(16),
            CreatedAt = reader.GetString(17)
        };
    }
}
