using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 工作模式 Run 级上下文服务。
/// A/C 层按 RunId 保存可恢复上下文；B 层不使用此服务。
/// </summary>
public sealed class WorkTaskContextService(CortanaDbContext db)
{
    private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>追加一条 Run 级上下文消息。</summary>
    public long AppendMessage(string taskId, string runId, string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var sequence = GetNextMessageSequence(taskId, runId);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO WorkTaskContextMessages (TaskId, RunId, Sequence, Role, Content, CreatedAt)
            VALUES (@TaskId, @RunId, @Sequence, @Role, @Content, @CreatedAt)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@RunId", runId);
                cmd.Parameters.AddWithValue("@Sequence", sequence);
                cmd.Parameters.AddWithValue("@Role", role.Trim());
                cmd.Parameters.AddWithValue("@Content", content);
                cmd.Parameters.AddWithValue("@CreatedAt", now);
            });

        return sequence;
    }

    /// <summary>读取指定 Run 的全部上下文消息。</summary>
    public List<WorkTaskContextMessageEntity> ListMessages(string taskId, string runId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(runId))
        {
            return [];
        }

        return _db.Query("""
            SELECT * FROM WorkTaskContextMessages
            WHERE TaskId = @TaskId AND RunId = @RunId
            ORDER BY Sequence ASC
            """,
            ReadMessage,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@RunId", runId);
            });
    }

    /// <summary>读取指定任务的全部 Run 级上下文消息。</summary>
    public List<WorkTaskContextMessageEntity> ListMessagesByTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return [];
        }

        return _db.Query("""
            SELECT * FROM WorkTaskContextMessages
            WHERE TaskId = @TaskId
            ORDER BY RunId ASC, Sequence ASC
            """,
            ReadMessage,
            cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
    }

    /// <summary>插入一个 Run 级压缩段。</summary>
    public void AddSegment(WorkTaskContextSegmentEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.RunId);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedAt = now;
        entity.UpdatedAt = now;

        _db.Execute("""
            INSERT INTO WorkTaskContextSegments (
                Id, TaskId, RunId, SegmentIndex, StartSequence, EndSequence,
                Summary, OriginalMessageCount, ModelName, CreatedAt, UpdatedAt)
            VALUES (
                @Id, @TaskId, @RunId, @SegmentIndex, @StartSequence, @EndSequence,
                @Summary, @OriginalMessageCount, @ModelName, @CreatedAt, @UpdatedAt)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", entity.Id);
                cmd.Parameters.AddWithValue("@TaskId", entity.TaskId);
                cmd.Parameters.AddWithValue("@RunId", entity.RunId);
                cmd.Parameters.AddWithValue("@SegmentIndex", entity.SegmentIndex);
                cmd.Parameters.AddWithValue("@StartSequence", entity.StartSequence);
                cmd.Parameters.AddWithValue("@EndSequence", entity.EndSequence);
                cmd.Parameters.AddWithValue("@Summary", entity.Summary);
                cmd.Parameters.AddWithValue("@OriginalMessageCount", entity.OriginalMessageCount);
                cmd.Parameters.AddWithValue("@ModelName", entity.ModelName);
                cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt);
                cmd.Parameters.AddWithValue("@UpdatedAt", entity.UpdatedAt);
            });
    }

    /// <summary>读取指定 Run 的压缩段。</summary>
    public List<WorkTaskContextSegmentEntity> ListSegments(string taskId, string runId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(runId))
        {
            return [];
        }

        return _db.Query("""
            SELECT * FROM WorkTaskContextSegments
            WHERE TaskId = @TaskId AND RunId = @RunId
            ORDER BY SegmentIndex ASC
            """,
            ReadSegment,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@RunId", runId);
            });
    }

    /// <summary>删除指定任务的所有上下文数据。</summary>
    public int DeleteByTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return 0;
        }

        return _db.ExecuteInTransaction((_, _) =>
        {
            var segments = _db.Execute(
                "DELETE FROM WorkTaskContextSegments WHERE TaskId = @TaskId",
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
            var messages = _db.Execute(
                "DELETE FROM WorkTaskContextMessages WHERE TaskId = @TaskId",
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
            return segments + messages;
        });
    }

    private long GetNextMessageSequence(string taskId, string runId)
    {
        var max = _db.ExecuteScalar<long>(
            """
            SELECT IFNULL(MAX(Sequence), 0)
            FROM WorkTaskContextMessages
            WHERE TaskId = @TaskId AND RunId = @RunId
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@RunId", runId);
            });
        return max + 1;
    }

    private static WorkTaskContextMessageEntity ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        TaskId = r.GetString(r.GetOrdinal("TaskId")),
        RunId = r.GetString(r.GetOrdinal("RunId")),
        Sequence = r.GetInt64(r.GetOrdinal("Sequence")),
        Role = r.GetString(r.GetOrdinal("Role")),
        Content = r.GetString(r.GetOrdinal("Content")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
    };

    private static WorkTaskContextSegmentEntity ReadSegment(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        TaskId = r.GetString(r.GetOrdinal("TaskId")),
        RunId = r.GetString(r.GetOrdinal("RunId")),
        SegmentIndex = r.GetInt32(r.GetOrdinal("SegmentIndex")),
        StartSequence = r.GetInt64(r.GetOrdinal("StartSequence")),
        EndSequence = r.GetInt64(r.GetOrdinal("EndSequence")),
        Summary = r.GetString(r.GetOrdinal("Summary")),
        OriginalMessageCount = r.GetInt32(r.GetOrdinal("OriginalMessageCount")),
        ModelName = r.GetString(r.GetOrdinal("ModelName")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
    };
}
