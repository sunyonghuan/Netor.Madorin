using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 主软件托管的背景任务服务（WorkBackgroundJobs 表）。
/// 仅服务"主软件托管的背景任务"——v1.0 即子智能体背景执行。
/// 不用于插件长任务（插件自治）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §八。
/// </summary>
public sealed class WorkBackgroundJobsService
{
    private readonly CortanaDbContext _db;

    public WorkBackgroundJobsService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public WorkBackgroundJobEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM WorkBackgroundJobs WHERE Id = @Id",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>列出某任务下指定状态的背景任务。</summary>
    public List<WorkBackgroundJobEntity> ListByTask(string taskId, string? state = null)
    {
        if (state is null)
        {
            return _db.Query(
                "SELECT * FROM WorkBackgroundJobs WHERE TaskId = @TaskId ORDER BY CreatedAt ASC",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        }
        return _db.Query("""
            SELECT * FROM WorkBackgroundJobs WHERE TaskId = @TaskId AND State = @State
            ORDER BY CreatedAt ASC
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@State", state);
            });
    }

    /// <summary>列出全局所有未结束的背景任务（应用启动时清理用）。</summary>
    public List<WorkBackgroundJobEntity> ListAllActive()
    {
        return _db.Query("""
            SELECT * FROM WorkBackgroundJobs
            WHERE State IN ('pending', 'running')
            ORDER BY CreatedAt ASC
            """,
            ReadEntity);
    }

    /// <summary>创建新背景任务（State=pending）。</summary>
    public void Create(WorkBackgroundJobEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        if (string.IsNullOrEmpty(entity.State))
            entity.State = WorkBackgroundJobStates.Pending;
        _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
    }

    /// <summary>从 pending 切换到 running。</summary>
    public void SetRunning(string id)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET State = @S, UpdatedAt = @Now WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@S", WorkBackgroundJobStates.Running);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>更新进度描述（不改 State）。</summary>
    public void UpdateProgress(string id, string description)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET ProgressDescription = @Desc, UpdatedAt = @Now WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Desc", description);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    public void SetCompleted(string id, string? resultJson)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET State = @S, ResultJson = @R, UpdatedAt = @Now, CompletedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@S", WorkBackgroundJobStates.Completed);
                cmd.Parameters.AddWithValue("@R", (object?)resultJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    public void SetFailed(string id, string error)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET State = @S, Error = @E, UpdatedAt = @Now, CompletedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@S", WorkBackgroundJobStates.Failed);
                cmd.Parameters.AddWithValue("@E", error);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    public void SetCancelled(string id)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET State = @S, UpdatedAt = @Now, CompletedAt = @Now WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@S", WorkBackgroundJobStates.Cancelled);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>
    /// 应用启动时统一清理：所有 pending/running 状态的背景任务标记为 failed。
    /// 与 <see cref="WorkTaskService.MarkOrphaned"/> 一致——不自动重启，等用户决策。
    /// </summary>
    public void MarkAllActiveAsFailed(string reason)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkBackgroundJobs SET State = 'failed', Error = @E, UpdatedAt = @Now, CompletedAt = @Now
            WHERE State IN ('pending', 'running')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@E", reason);
                cmd.Parameters.AddWithValue("@Now", now);
            });
    }

    private const string InsertSql = """
        INSERT INTO WorkBackgroundJobs (
            Id, TaskId, JobKind, OwnerName, State, InputJson, ProgressDescription,
            ResultJson, Error, CreatedAt, UpdatedAt, CompletedAt
        ) VALUES (
            @Id, @TaskId, @JobKind, @OwnerName, @State, @InputJson, @ProgressDescription,
            @ResultJson, @Error, @CreatedAt, @UpdatedAt, @CompletedAt
        )
        """;

    private static WorkBackgroundJobEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        TaskId = r.GetString(r.GetOrdinal("TaskId")),
        JobKind = r.GetString(r.GetOrdinal("JobKind")),
        OwnerName = r.IsDBNull(r.GetOrdinal("OwnerName")) ? null : r.GetString(r.GetOrdinal("OwnerName")),
        State = r.GetString(r.GetOrdinal("State")),
        InputJson = r.GetString(r.GetOrdinal("InputJson")),
        ProgressDescription = r.IsDBNull(r.GetOrdinal("ProgressDescription")) ? null : r.GetString(r.GetOrdinal("ProgressDescription")),
        ResultJson = r.IsDBNull(r.GetOrdinal("ResultJson")) ? null : r.GetString(r.GetOrdinal("ResultJson")),
        Error = r.IsDBNull(r.GetOrdinal("Error")) ? null : r.GetString(r.GetOrdinal("Error")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
        CompletedAt = r.IsDBNull(r.GetOrdinal("CompletedAt")) ? null : r.GetInt64(r.GetOrdinal("CompletedAt")),
    };

    private static void BindEntity(SqliteCommand cmd, WorkBackgroundJobEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@TaskId", e.TaskId);
        cmd.Parameters.AddWithValue("@JobKind", e.JobKind);
        cmd.Parameters.AddWithValue("@OwnerName", (object?)e.OwnerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@State", e.State);
        cmd.Parameters.AddWithValue("@InputJson", e.InputJson);
        cmd.Parameters.AddWithValue("@ProgressDescription", (object?)e.ProgressDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResultJson", (object?)e.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Error", (object?)e.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", e.UpdatedAt);
        cmd.Parameters.AddWithValue("@CompletedAt", (object?)e.CompletedAt ?? DBNull.Value);
    }
}
