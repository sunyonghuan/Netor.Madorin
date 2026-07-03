using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 工作模式任务执行日志服务（WorkExecutionLogs 表）。
/// 每次 Append 同步更新 <see cref="WorkTaskService"/> 的心跳。
/// </summary>
public sealed class WorkExecutionLogService
{
    private readonly CortanaDbContext _db;
    private readonly WorkTaskService _taskService;

    public WorkExecutionLogService(CortanaDbContext db, WorkTaskService taskService)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
    }

    /// <summary>追加一条日志，自动分配 Sequence + 更新任务心跳。</summary>
    /// <returns>新插入记录的自增 Id。</returns>
    public long Append(string taskId, string logType, string content)
    {
        var nextSeq = NextSequence(taskId);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        _db.Execute("""
            INSERT INTO WorkExecutionLogs (TaskId, Sequence, LogType, Content, CreatedAt)
            VALUES (@TaskId, @Sequence, @LogType, @Content, @CreatedAt)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@Sequence", nextSeq);
                cmd.Parameters.AddWithValue("@LogType", logType);
                cmd.Parameters.AddWithValue("@Content", content);
                cmd.Parameters.AddWithValue("@CreatedAt", now);
            });

        // 同步刷新心跳（[16 文档 §八] 心跳放在 AppendAsync 内部统一处理）
        _taskService.Touch(taskId);

        return _db.ExecuteScalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>查询任务的全部日志（UI 重建时间线用，按序号升序）。</summary>
    public List<WorkExecutionLogEntity> ListByTask(string taskId)
    {
        return _db.Query("""
            SELECT * FROM WorkExecutionLogs WHERE TaskId = @TaskId ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
    }

    /// <summary>查询任务最近 N 条日志（死循环检测用，按序号倒序）。</summary>
    public List<WorkExecutionLogEntity> RecentByTask(string taskId, int take)
    {
        return _db.Query("""
            SELECT * FROM WorkExecutionLogs WHERE TaskId = @TaskId
            ORDER BY Sequence DESC LIMIT @Take
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@Take", take);
            });
    }

    /// <summary>下一个序号（任务内单调递增）。</summary>
    public long NextSequence(string taskId)
    {
        var max = _db.ExecuteScalar<long>(
            "SELECT IFNULL(MAX(Sequence), 0) FROM WorkExecutionLogs WHERE TaskId = @TaskId",
            cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        return max + 1;
    }

    private static WorkExecutionLogEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        TaskId = r.GetString(r.GetOrdinal("TaskId")),
        Sequence = r.GetInt64(r.GetOrdinal("Sequence")),
        LogType = r.GetString(r.GetOrdinal("LogType")),
        Content = r.GetString(r.GetOrdinal("Content")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
    };
}
