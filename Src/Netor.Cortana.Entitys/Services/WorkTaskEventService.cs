using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 工作模式任务事件队列服务。
/// </summary>
public sealed class WorkTaskEventService(CortanaDbContext db)
{
    public WorkTaskEventEntity Create(string taskId, string kind, string message, string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entity = new WorkTaskEventEntity
        {
            TaskId = taskId,
            Kind = kind.Trim(),
            Message = message.Trim(),
            RunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim(),
            CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds()
        };

        db.Execute("""
            INSERT INTO WorkTaskEvents (Id, TaskId, RunId, Kind, Message, CreatedAt, ReadAt)
            VALUES (@Id, @TaskId, @RunId, @Kind, @Message, @CreatedAt, @ReadAt)
            """,
            cmd => BindEntity(cmd, entity));

        return entity;
    }

    public List<WorkTaskEventEntity> ListUnread(string taskId, string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return string.IsNullOrWhiteSpace(runId)
            ? db.Query("""
                SELECT * FROM WorkTaskEvents
                WHERE TaskId = @TaskId AND ReadAt IS NULL
                ORDER BY CreatedAt ASC
                """,
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId))
            : db.Query("""
                SELECT * FROM WorkTaskEvents
                WHERE TaskId = @TaskId AND (RunId = @RunId OR RunId IS NULL) AND ReadAt IS NULL
                ORDER BY CreatedAt ASC
                """,
                ReadEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@TaskId", taskId);
                    cmd.Parameters.AddWithValue("@RunId", runId);
                });
    }

    public void MarkRead(IEnumerable<string> ids)
    {
        var idList = ids
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (idList.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        foreach (var id in idList)
        {
            db.Execute(
                "UPDATE WorkTaskEvents SET ReadAt = @Now WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", id);
                });
        }
    }

    private static WorkTaskEventEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        TaskId = r.GetString(r.GetOrdinal("TaskId")),
        RunId = ReadNullableString(r, "RunId"),
        Kind = r.GetString(r.GetOrdinal("Kind")),
        Message = r.GetString(r.GetOrdinal("Message")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        ReadAt = ReadNullableInt64(r, "ReadAt")
    };

    private static void BindEntity(SqliteCommand cmd, WorkTaskEventEntity entity)
    {
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        cmd.Parameters.AddWithValue("@TaskId", entity.TaskId);
        cmd.Parameters.AddWithValue("@RunId", (object?)entity.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Kind", entity.Kind);
        cmd.Parameters.AddWithValue("@Message", entity.Message);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt);
        cmd.Parameters.AddWithValue("@ReadAt", (object?)entity.ReadAt ?? DBNull.Value);
    }

    private static string? ReadNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static long? ReadNullableInt64(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }
}
