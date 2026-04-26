using Cortana.Plugins.Memory.Models;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_fragments 表的数据操作服务，负责具体记忆片段的增删改查和基础检索。
/// </summary>
/// <remarks>
/// 记忆片段通常来源于观察记录抽取结果，是可被召回、强化、衰减和进一步抽象的基础记忆单元。
/// </remarks>
public sealed class MemoryFragmentsTable(IMemoryDatabase database)
{
    private const string Columns = "id, agentId, workspaceId, memoryType, topic, title, summary, detail, keywordsJson, tagsJson, entitiesJson, sourceObservationIdsJson, sourceSessionIdsJson, sourceTurnIdsJson, importance, confidence, emotionalWeight, novelty, salienceScore, retentionScore, decayRate, accessCount, reinforcementCount, contradictionCount, clarityLevel, confirmationState, lifecycleState, lastAccessedAt, lastReinforcedAt, expiresAt, schemaVersion, recordVersion, compatibilityTagsJson, createdAt, updatedAt";

    /// <summary>
    /// 插入或替换一个记忆片段。
    /// </summary>
    /// <param name="fragment">需要写入 memory_fragments 表的记忆片段。</param>
    public void Upsert(MemoryFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_fragments (" + Columns + ") VALUES (@id,@agent,@workspace,@type,@topic,@title,@summary,@detail,@keywords,@tags,@entities,@sourceObservations,@sourceSessions,@sourceTurns,@importance,@confidence,@emotional,@novelty,@salience,@retention,@decay,@access,@reinforcement,@contradiction,@clarity,@confirmation,@lifecycle,@lastAccessed,@lastReinforced,@expires,@schema,@record,@compatibility,@created,@updated)";
        Bind(command, fragment);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 根据记忆片段标识读取单条记录。
    /// </summary>
    /// <param name="id">记忆片段标识。</param>
    /// <returns>找到的记忆片段；不存在时返回 null。</returns>
    public MemoryFragment? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("记忆片段标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM memory_fragments WHERE id = @id LIMIT 1";
        command.AddParameter("@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 按智能体和工作区筛选最近更新的记忆片段。
    /// </summary>
    /// <param name="agentId">可选的智能体标识；为 null 时不过滤智能体。</param>
    /// <param name="workspaceId">可选的工作区标识；为 null 时不过滤工作区。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按更新时间倒序排列的记忆片段列表。</returns>
    public IReadOnlyList<MemoryFragment> List(string? agentId, string? workspaceId, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_fragments
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
    /// 获取记忆片段表中已出现过的智能体标识集合。
    /// </summary>
    /// <returns>去重后的非空智能体标识列表。</returns>
    public IReadOnlyList<string> GetDistinctAgentIds()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT agentId FROM memory_fragments WHERE agentId IS NOT NULL AND agentId <> ''";

        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// 查找与指定摘要完全一致的同类型记忆片段，用于合并或去重判断。
    /// </summary>
    /// <param name="agentId">智能体标识。</param>
    /// <param name="workspaceId">可选的工作区标识。</param>
    /// <param name="memoryType">记忆类型。</param>
    /// <param name="summary">用于匹配的记忆摘要。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按更新时间倒序排列的相似记忆片段列表。</returns>
    public IReadOnlyList<MemoryFragment> SearchSimilar(string agentId, string? workspaceId, string memoryType, string summary, int limit)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("智能体标识不能为空。", nameof(agentId));
        if (string.IsNullOrWhiteSpace(memoryType)) throw new ArgumentException("记忆类型不能为空。", nameof(memoryType));
        if (string.IsNullOrWhiteSpace(summary) || limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_fragments
WHERE agentId = @agent
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
  AND memoryType = @type
  AND summary = @summary
ORDER BY updatedAt DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@type", memoryType);
        command.AddParameter("@summary", summary);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 获取适合参与抽象整理的高显著性记忆片段。
    /// </summary>
    /// <param name="agentId">智能体标识。</param>
    /// <param name="workspaceId">可选的工作区标识。</param>
    /// <param name="topic">可选的话题；为 null 时不过滤话题。</param>
    /// <param name="limit">最多返回的记录数量。</param>
    /// <returns>按显著性和更新时间排序的候选记忆片段列表。</returns>
    public IReadOnlyList<MemoryFragment> GetForAbstraction(string agentId, string? workspaceId, string? topic, int limit)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("智能体标识不能为空。", nameof(agentId));
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_fragments
WHERE agentId = @agent
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
  AND (@topic IS NULL OR topic = @topic)
ORDER BY salienceScore DESC, updatedAt DESC
LIMIT @limit";
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);
        command.AddParameter("@topic", topic);
        command.AddParameter("@limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// 记录记忆片段被访问的事实，并更新访问次数和最后访问时间。
    /// </summary>
    /// <param name="id">记忆片段标识。</param>
    /// <param name="accessedAt">访问发生时间。</param>
    public void RecordAccess(string id, string accessedAt)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("记忆片段标识不能为空。", nameof(id));
        if (string.IsNullOrWhiteSpace(accessedAt)) throw new ArgumentException("访问时间不能为空。", nameof(accessedAt));

        database.Execute("UPDATE memory_fragments SET accessCount = accessCount + 1, lastAccessedAt = @accessedAt, updatedAt = @accessedAt WHERE id = @id", command =>
        {
            command.AddParameter("@accessedAt", accessedAt);
            command.AddParameter("@id", id);
        });
    }

    /// <summary>
    /// 删除指定标识的记忆片段。
    /// </summary>
    /// <param name="id">记忆片段标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("记忆片段标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_fragments WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆片段模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆片段模型。</returns>
    public static MemoryFragment Read(SqliteDataReader reader)
    {
        return new MemoryFragment
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            WorkspaceId = reader.GetNullableString(2),
            MemoryType = reader.GetString(3),
            Topic = reader.GetString(4),
            Title = reader.GetNullableString(5),
            Summary = reader.GetString(6),
            Detail = reader.GetNullableString(7),
            KeywordsJson = reader.GetNullableString(8),
            TagsJson = reader.GetNullableString(9),
            EntitiesJson = reader.GetNullableString(10),
            SourceObservationIdsJson = reader.GetString(11),
            SourceSessionIdsJson = reader.GetNullableString(12),
            SourceTurnIdsJson = reader.GetNullableString(13),
            Importance = reader.GetDouble(14),
            Confidence = reader.GetDouble(15),
            EmotionalWeight = reader.GetDouble(16),
            Novelty = reader.GetDouble(17),
            SalienceScore = reader.GetDouble(18),
            RetentionScore = reader.GetDouble(19),
            DecayRate = reader.GetDouble(20),
            AccessCount = reader.GetInt32(21),
            ReinforcementCount = reader.GetInt32(22),
            ContradictionCount = reader.GetInt32(23),
            ClarityLevel = reader.GetString(24),
            ConfirmationState = reader.GetString(25),
            LifecycleState = reader.GetString(26),
            LastAccessedAt = reader.GetNullableString(27),
            LastReinforcedAt = reader.GetNullableString(28),
            ExpiresAt = reader.GetNullableString(29),
            SchemaVersion = reader.GetInt32(30),
            RecordVersion = reader.GetInt32(31),
            CompatibilityTagsJson = reader.GetNullableString(32),
            CreatedAt = reader.GetString(33),
            UpdatedAt = reader.GetString(34)
        };
    }

    /// <summary>
    /// 执行命令并把完整结果集映射为记忆片段列表。
    /// </summary>
    /// <param name="command">已经设置好 SQL 和参数的 SQLite 命令。</param>
    /// <returns>记忆片段列表。</returns>
    private static IReadOnlyList<MemoryFragment> ReadAll(SqliteCommand command)
    {
        var fragments = new List<MemoryFragment>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) fragments.Add(Read(reader));
        return fragments;
    }

    /// <summary>
    /// 将记忆片段字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="fragment">参数来源记忆片段。</param>
    private static void Bind(SqliteCommand command, MemoryFragment fragment)
    {
        command.AddParameter("@id", fragment.Id);
        command.AddParameter("@agent", fragment.AgentId);
        command.AddParameter("@workspace", fragment.WorkspaceId);
        command.AddParameter("@type", fragment.MemoryType);
        command.AddParameter("@topic", fragment.Topic);
        command.AddParameter("@title", fragment.Title);
        command.AddParameter("@summary", fragment.Summary);
        command.AddParameter("@detail", fragment.Detail);
        command.AddParameter("@keywords", fragment.KeywordsJson);
        command.AddParameter("@tags", fragment.TagsJson);
        command.AddParameter("@entities", fragment.EntitiesJson);
        command.AddParameter("@sourceObservations", fragment.SourceObservationIdsJson);
        command.AddParameter("@sourceSessions", fragment.SourceSessionIdsJson);
        command.AddParameter("@sourceTurns", fragment.SourceTurnIdsJson);
        command.AddParameter("@importance", fragment.Importance);
        command.AddParameter("@confidence", fragment.Confidence);
        command.AddParameter("@emotional", fragment.EmotionalWeight);
        command.AddParameter("@novelty", fragment.Novelty);
        command.AddParameter("@salience", fragment.SalienceScore);
        command.AddParameter("@retention", fragment.RetentionScore);
        command.AddParameter("@decay", fragment.DecayRate);
        command.AddParameter("@access", fragment.AccessCount);
        command.AddParameter("@reinforcement", fragment.ReinforcementCount);
        command.AddParameter("@contradiction", fragment.ContradictionCount);
        command.AddParameter("@clarity", fragment.ClarityLevel);
        command.AddParameter("@confirmation", fragment.ConfirmationState);
        command.AddParameter("@lifecycle", fragment.LifecycleState);
        command.AddParameter("@lastAccessed", fragment.LastAccessedAt);
        command.AddParameter("@lastReinforced", fragment.LastReinforcedAt);
        command.AddParameter("@expires", fragment.ExpiresAt);
        command.AddParameter("@schema", fragment.SchemaVersion);
        command.AddParameter("@record", fragment.RecordVersion);
        command.AddParameter("@compatibility", fragment.CompatibilityTagsJson);
        command.AddParameter("@created", fragment.CreatedAt);
        command.AddParameter("@updated", fragment.UpdatedAt);
    }
}
