using Cortana.Plugins.Memory.Processing;
using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// memory_processing_states 表的数据操作服务，负责保存后台记忆处理器的处理进度和运行状态。
/// </summary>
/// <remarks>
/// 该表用于让长期运行的记忆整理服务能够从上次处理位置继续执行，并记录处理计数、锁定时间和最近错误。
/// </remarks>
public sealed class MemoryProcessingStatesTable(IMemoryDatabase database)
{
    private const string Columns = "id, processorName, agentId, workspaceId, state, lastObservationTimestamp, lastObservationId, processedCount, createdFragmentCount, mergedFragmentCount, createdAbstractionCount, lastError, lockedUntil, createdAt, updatedAt";

    /// <summary>
    /// 插入或替换一条处理状态记录。
    /// </summary>
    /// <param name="state">需要写入 memory_processing_states 表的处理状态。</param>
    public void Upsert(MemoryProcessingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.ProcessorName)) throw new ArgumentException("处理器名称不能为空。", nameof(state));
        if (string.IsNullOrWhiteSpace(state.Id)) state.Id = CreateId(state.ProcessorName, state.AgentId, state.WorkspaceId);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO memory_processing_states (" + Columns + ") VALUES (@id,@processor,@agent,@workspace,@state,@lastTimestamp,@lastId,@processed,@createdFragments,@mergedFragments,@createdAbstractions,@lastError,@lockedUntil,@created,@updated)";
        Bind(command, state);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取指定处理器、智能体和工作区对应的处理状态。
    /// </summary>
    /// <param name="processorName">处理器名称。</param>
    /// <param name="agentId">可选的智能体标识。</param>
    /// <param name="workspaceId">可选的工作区标识。</param>
    /// <returns>已保存的处理状态；不存在时返回默认空闲状态。</returns>
    public MemoryProcessingState Get(string processorName, string? agentId, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(processorName)) throw new ArgumentException("处理器名称不能为空。", nameof(processorName));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_processing_states
WHERE processorName = @processor
  AND ifnull(agentId, '') = ifnull(@agent, '')
  AND ifnull(workspaceId, '') = ifnull(@workspace, '')
LIMIT 1";
        command.AddParameter("@processor", processorName);
        command.AddParameter("@agent", agentId);
        command.AddParameter("@workspace", workspaceId);

        using var reader = command.ExecuteReader();
        if (reader.Read()) return Read(reader);

        var now = DateTimeOffset.UtcNow.ToString("O");
        return new MemoryProcessingState
        {
            Id = CreateId(processorName, agentId, workspaceId),
            ProcessorName = processorName,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            State = "idle",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// 按处理器名称筛选处理状态记录。
    /// </summary>
    /// <param name="processorName">可选的处理器名称；为 null 时不过滤处理器。</param>
    /// <param name="limit">最多返回的状态记录数量。</param>
    /// <returns>按更新时间倒序排列的处理状态列表。</returns>
    public IReadOnlyList<MemoryProcessingState> List(string? processorName, int limit)
    {
        if (limit <= 0) return [];

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + @" FROM memory_processing_states
WHERE (@processor IS NULL OR processorName = @processor)
ORDER BY updatedAt DESC
LIMIT @limit";
        command.AddParameter("@processor", processorName);
        command.AddParameter("@limit", limit);

        var list = new List<MemoryProcessingState>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 删除指定标识的处理状态记录。
    /// </summary>
    /// <param name="id">处理状态标识。</param>
    /// <returns>成功删除记录时返回 true；未找到记录时返回 false。</returns>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("处理状态标识不能为空。", nameof(id));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_processing_states WHERE id = @id";
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 根据处理器名称、智能体和工作区生成稳定的处理状态主键。
    /// </summary>
    /// <param name="processorName">处理器名称。</param>
    /// <param name="agentId">可选的智能体标识。</param>
    /// <param name="workspaceId">可选的工作区标识。</param>
    /// <returns>由处理器、智能体和工作区组成的处理状态标识。</returns>
    public static string CreateId(string processorName, string? agentId, string? workspaceId)
    {
        return $"{processorName}:{agentId ?? string.Empty}:{workspaceId ?? string.Empty}";
    }

    /// <summary>
    /// 将处理状态字段绑定到 SQLite 命令参数。
    /// </summary>
    /// <param name="command">待绑定参数的 SQLite 命令。</param>
    /// <param name="state">参数来源处理状态。</param>
    private static void Bind(SqliteCommand command, MemoryProcessingState state)
    {
        command.AddParameter("@id", state.Id);
        command.AddParameter("@processor", state.ProcessorName);
        command.AddParameter("@agent", state.AgentId);
        command.AddParameter("@workspace", state.WorkspaceId);
        command.AddParameter("@state", state.State);
        command.AddParameter("@lastTimestamp", state.LastObservationTimestamp);
        command.AddParameter("@lastId", state.LastObservationId);
        command.AddParameter("@processed", state.ProcessedCount);
        command.AddParameter("@createdFragments", state.CreatedFragmentCount);
        command.AddParameter("@mergedFragments", state.MergedFragmentCount);
        command.AddParameter("@createdAbstractions", state.CreatedAbstractionCount);
        command.AddParameter("@lastError", state.LastError);
        command.AddParameter("@lockedUntil", state.LockedUntil);
        command.AddParameter("@created", state.CreatedAt);
        command.AddParameter("@updated", state.UpdatedAt);
    }

    /// <summary>
    /// 将当前 SQLite 读取行映射为记忆处理状态模型。
    /// </summary>
    /// <param name="reader">定位到有效行的 SQLite 数据读取器。</param>
    /// <returns>记忆处理状态模型。</returns>
    private static MemoryProcessingState Read(SqliteDataReader reader)
    {
        return new MemoryProcessingState
        {
            Id = reader.GetString(0),
            ProcessorName = reader.GetString(1),
            AgentId = reader.GetNullableString(2),
            WorkspaceId = reader.GetNullableString(3),
            State = reader.GetString(4),
            LastObservationTimestamp = reader.GetInt64(5),
            LastObservationId = reader.GetNullableString(6),
            ProcessedCount = reader.GetInt32(7),
            CreatedFragmentCount = reader.GetInt32(8),
            MergedFragmentCount = reader.GetInt32(9),
            CreatedAbstractionCount = reader.GetInt32(10),
            LastError = reader.GetNullableString(11),
            LockedUntil = reader.GetNullableString(12),
            CreatedAt = reader.GetString(13),
            UpdatedAt = reader.GetString(14)
        };
    }
}
