using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_abstractions 表的数据操作服务，负责抽象记忆的增删改查和访问记录更新。
/// </summary>
/// <remarks>
/// 抽象记忆由多个记忆片段归纳而来，用于保存更稳定的结论、偏好、规则或长期事实。
/// </remarks>
public sealed class MemoryAbstractionsTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, workspaceId, abstractionType, title, statement, summary, supportingMemoryIdsJson, counterMemoryIdsJson, keywordsJson, tagsJson, importance, confidence, stabilityScore, retentionScore, decayRate, accessCount, reinforcementCount, contradictionCount, clarityLevel, confirmationState, lifecycleState, lastValidatedAt, lastAccessedAt, expiresAt, schemaVersion, recordVersion, compatibilityTagsJson, createdAt, updatedAt";

    /// <summary>
    /// 插入或替换一条抽象记忆。
    /// </summary>
    /// <param name="abstraction">需要写入 memory_abstractions 表的抽象记忆。</param>
    public void Upsert(MemoryAbstraction abstraction)
    {
        ArgumentNullException.ThrowIfNull(abstraction);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_abstractions (" + Columns + ") VALUES (@id,@agent,@workspace,@type,@title,@statement,@summary,@supporting,@counter,@keywords,@tags,@importance,@confidence,@stability,@retention,@decay,@access,@reinforcement,@contradiction,@clarity,@confirmation,@lifecycle,@lastValidated,@lastAccessed,@expires,@schema,@record,@compatibility,@created,@updated)";
        Bind(command, abstraction);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据抽象记忆标识读取单条记录。
    /// </summary>
    /// <param name="id">抽象记忆标识。</param>
    /// <returns>找到的抽象记忆；不存在时返回 null。</returns>
    public MemoryAbstraction? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("抽象记忆标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_abstractions WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 按智能体和工作区筛选最近更新的抽象记忆。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="workspaceId">可选的工作区标识；为 null 时不过滤工作区。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按更新时间倒序排列的抽象记忆列表。</returns>
    public IReadOnlyList<MemoryAbstraction> List(string? agentId, string? workspaceId, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_abstractions
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
ORDER BY updatedAt DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 记录抽象记忆被访问的事实，并更新访问次数和最后访问时间。
    /// </summary>
    /// <param name="id">抽象记忆标识。</param>
    /// <param name="accessedAt">访问发生时间。</param>
    public void RecordAccess(string id, string accessedAt)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("抽象记忆标识不能为空。", nameof(id));
        if (string.IsNullOrWhiteSpace(accessedAt)) throw new ArgumentException("访问时间不能为空。", nameof(accessedAt));

        database.Execute("UPDATE memory_abstractions SET accessCount = accessCount + 1, lastAccessedAt = @accessedAt, updatedAt = @accessedAt WHERE id = @id", command =>
        {
            command.AddParameter("@accessedAt", accessedAt);
            command.AddParameter("@id", id);
        });
    }

    /// <summary>
    /// 删除指定标识的抽象记忆。
    /// </summary>
    /// <param name="id">抽象记忆标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("抽象记忆标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_abstractions WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 执行命令并把完整结果集映射为抽象记忆列表。
    /// </summary>
    /// <param name="command">已经设置好 SQL 和参数的 SQLite 命令。</param>
    /// <returns>抽象记忆列表。</returns>
    private static IReadOnlyList<MemoryAbstraction> ReadAll(SqliteCommand command)
    {
        var abstractions = new List<MemoryAbstraction>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) abstractions.Add(Read(reader));
        return abstractions;
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为抽象记忆模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>抽象记忆模型。</returns>
    private static MemoryAbstraction Read(SqliteDataReader reader)
    {
        return new MemoryAbstraction
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            WorkspaceId = reader.GetNullableString(2),
            AbstractionType = reader.GetString(3),
            Title = reader.GetNullableString(4),
            Statement = reader.GetString(5),
            Summary = reader.GetString(6),
            SupportingMemoryIdsJson = reader.GetString(7),
            CounterMemoryIdsJson = reader.GetNullableString(8),
            KeywordsJson = reader.GetNullableString(9),
            TagsJson = reader.GetNullableString(10),
            Importance = reader.GetDouble(11),
            Confidence = reader.GetDouble(12),
            StabilityScore = reader.GetDouble(13),
            RetentionScore = reader.GetDouble(14),
            DecayRate = reader.GetDouble(15),
            AccessCount = reader.GetInt32(16),
            ReinforcementCount = reader.GetInt32(17),
            ContradictionCount = reader.GetInt32(18),
            ClarityLevel = reader.GetString(19),
            ConfirmationState = reader.GetString(20),
            LifecycleState = reader.GetString(21),
            LastValidatedAt = reader.GetNullableString(22),
            LastAccessedAt = reader.GetNullableString(23),
            ExpiresAt = reader.GetNullableString(24),
            SchemaVersion = reader.GetInt32(25),
            RecordVersion = reader.GetInt32(26),
            CompatibilityTagsJson = reader.GetNullableString(27),
            CreatedAt = reader.GetString(28),
            UpdatedAt = reader.GetString(29)
        };
    }

    /// <summary>
    /// 将抽象记忆字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="abstraction">参数来源抽象记忆。</param>
    private static void Bind(SqliteCommand command, MemoryAbstraction abstraction)
    {
        command.AddParameter("@id", abstraction.Id);
        command.AddParameter("@agent", abstraction.AgentId);
        command.AddParameter("@workspace", abstraction.WorkspaceId);
        command.AddParameter("@type", abstraction.AbstractionType);
        command.AddParameter("@title", abstraction.Title);
        command.AddParameter("@statement", abstraction.Statement);
        command.AddParameter("@summary", abstraction.Summary);
        command.AddParameter("@supporting", abstraction.SupportingMemoryIdsJson);
        command.AddParameter("@counter", abstraction.CounterMemoryIdsJson);
        command.AddParameter("@keywords", abstraction.KeywordsJson);
        command.AddParameter("@tags", abstraction.TagsJson);
        command.AddParameter("@importance", abstraction.Importance);
        command.AddParameter("@confidence", abstraction.Confidence);
        command.AddParameter("@stability", abstraction.StabilityScore);
        command.AddParameter("@retention", abstraction.RetentionScore);
        command.AddParameter("@decay", abstraction.DecayRate);
        command.AddParameter("@access", abstraction.AccessCount);
        command.AddParameter("@reinforcement", abstraction.ReinforcementCount);
        command.AddParameter("@contradiction", abstraction.ContradictionCount);
        command.AddParameter("@clarity", abstraction.ClarityLevel);
        command.AddParameter("@confirmation", abstraction.ConfirmationState);
        command.AddParameter("@lifecycle", abstraction.LifecycleState);
        command.AddParameter("@lastValidated", abstraction.LastValidatedAt);
        command.AddParameter("@lastAccessed", abstraction.LastAccessedAt);
        command.AddParameter("@expires", abstraction.ExpiresAt);
        command.AddParameter("@schema", abstraction.SchemaVersion);
        command.AddParameter("@record", abstraction.RecordVersion);
        command.AddParameter("@compatibility", abstraction.CompatibilityTagsJson);
        command.AddParameter("@created", abstraction.CreatedAt);
        command.AddParameter("@updated", abstraction.UpdatedAt);
    }
}
