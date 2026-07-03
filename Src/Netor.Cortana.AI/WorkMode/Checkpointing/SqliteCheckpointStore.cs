using System.Text.Json;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.WorkMode.Checkpointing;

/// <summary>
/// 基于 SQLite 的 Checkpoint 存储，复用现有 WorkflowCheckpoints 表。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §4。
/// </summary>
public sealed class SqliteCheckpointStore : ICheckpointStore<JsonElement>, IDisposable
{
    private readonly CortanaDbContext _db;

    public SqliteCheckpointStore(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement data, CheckpointInfo? checkpointInfo)
    {
        var cpId = checkpointInfo?.CheckpointId ?? Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, WorkModeJsonContext.Default.JsonElement);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        _db.Execute("""
            INSERT OR REPLACE INTO WorkflowCheckpoints (TaskId, CheckpointId, Payload, CreatedAt)
            VALUES (@TaskId, @CheckpointId, @Payload, @CreatedAt)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", sessionId);
                cmd.Parameters.AddWithValue("@CheckpointId", cpId);
                cmd.Parameters.AddWithValue("@Payload", payload);
                cmd.Parameters.AddWithValue("@CreatedAt", now);
            });

        return new ValueTask<CheckpointInfo>(new CheckpointInfo(cpId, sessionId));
    }

    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo checkpointInfo)
    {
        var payload = _db.QueryFirstOrDefault(
            "SELECT Payload FROM WorkflowCheckpoints WHERE TaskId = @TaskId AND CheckpointId = @CheckpointId",
            r => (byte[])r["Payload"],
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", sessionId);
                cmd.Parameters.AddWithValue("@CheckpointId", checkpointInfo.CheckpointId);
            });

        if (payload is null)
            return new ValueTask<JsonElement>(default(JsonElement));

        var element = JsonSerializer.Deserialize(payload, WorkModeJsonContext.Default.JsonElement);
        return new ValueTask<JsonElement>(element);
    }

    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? checkpointInfo)
    {
        var results = _db.Query(
            "SELECT CheckpointId FROM WorkflowCheckpoints WHERE TaskId = @TaskId ORDER BY CreatedAt DESC",
            r => new CheckpointInfo(r.GetString(r.GetOrdinal("CheckpointId")), sessionId),
            cmd => cmd.Parameters.AddWithValue("@TaskId", sessionId));

        return new ValueTask<IEnumerable<CheckpointInfo>>(results);
    }

    public void Dispose()
    {
    }
}
