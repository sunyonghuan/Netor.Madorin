using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 专家模式后台委派任务服务。
/// </summary>
public sealed class DelegatedAgentJobService
{
    private static readonly HashSet<string> TerminalStates =
    [
        DelegatedAgentJobStates.Completed,
        DelegatedAgentJobStates.Failed,
        DelegatedAgentJobStates.Cancelled
    ];

    private readonly CortanaDbContext _db;

    public DelegatedAgentJobService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public DelegatedAgentJobEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM DelegatedAgentJobs WHERE Id = @Id",
            ReadJob,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    public List<DelegatedAgentJobEntity> ListActive()
    {
        return _db.Query("""
            SELECT * FROM DelegatedAgentJobs
            WHERE State IN ('pending', 'running')
            ORDER BY CreatedAt ASC
            """,
            ReadJob);
    }

    public List<DelegatedAgentJobLogEntity> ListLogs(string jobId)
    {
        return _db.Query("""
            SELECT * FROM DelegatedAgentJobLogs
            WHERE JobId = @JobId
            ORDER BY Id ASC
            """,
            ReadLog,
            cmd => cmd.Parameters.AddWithValue("@JobId", jobId));
    }

    public void Create(DelegatedAgentJobEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(entity.State))
        {
            entity.State = DelegatedAgentJobStates.Pending;
        }

        _db.ExecuteInTransaction(connection =>
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = InsertJobSql;
            BindJob(insert, entity);
            insert.ExecuteNonQuery();

            InsertLog(connection, entity.Id, DelegatedAgentJobLogKinds.Started, "后台委派任务已创建。", now);
        });
    }

    public bool SetRunning(string id, string? progressDescription = null)
    {
        return TryUpdateNonTerminal(
            id,
            DelegatedAgentJobStates.Running,
            progressDescription ?? "后台委派任务已启动。",
            resultJson: null,
            error: null,
            completedAt: null,
            DelegatedAgentJobLogKinds.Started,
            progressDescription ?? "后台委派任务已启动。");
    }

    public bool UpdateProgress(string id, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var affected = _db.Execute("""
            UPDATE DelegatedAgentJobs
            SET ProgressDescription = @Desc, UpdatedAt = @Now
            WHERE Id = @Id AND State NOT IN ('completed', 'failed', 'cancelled')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Desc", description.Trim());
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });

        if (affected > 0)
        {
            AddLog(id, DelegatedAgentJobLogKinds.Progress, description.Trim(), now);
        }

        return affected > 0;
    }

    public bool Complete(string id, string resultJson)
    {
        return TryUpdateNonTerminal(
            id,
            DelegatedAgentJobStates.Completed,
            "后台委派任务已完成。",
            resultJson,
            error: null,
            completedAt: DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            DelegatedAgentJobLogKinds.Completed,
            "后台委派任务已完成。");
    }

    public bool Fail(string id, string error)
    {
        return TryUpdateNonTerminal(
            id,
            DelegatedAgentJobStates.Failed,
            "后台委派任务执行失败。",
            resultJson: null,
            error,
            completedAt: DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            DelegatedAgentJobLogKinds.Failed,
            error);
    }

    public bool Cancel(string id)
    {
        var current = GetById(id);
        if (current is null)
        {
            return false;
        }

        if (current.State == DelegatedAgentJobStates.Cancelled)
        {
            return true;
        }

        if (TerminalStates.Contains(current.State))
        {
            return false;
        }

        return TryUpdateNonTerminal(
            id,
            DelegatedAgentJobStates.Cancelled,
            "后台委派任务已取消。",
            resultJson: null,
            error: null,
            completedAt: DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            DelegatedAgentJobLogKinds.Cancelled,
            "后台委派任务已取消。");
    }

    public int MarkAllActiveAsFailed(string reason)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var activeJobs = ListActive();
        var affected = _db.Execute("""
            UPDATE DelegatedAgentJobs
            SET State = 'failed',
                Error = @Reason,
                ProgressDescription = '后台委派任务已停止。',
                UpdatedAt = @Now,
                CompletedAt = @Now
            WHERE State IN ('pending', 'running')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Reason", reason);
                cmd.Parameters.AddWithValue("@Now", now);
            });

        foreach (var job in activeJobs)
        {
            AddLog(job.Id, DelegatedAgentJobLogKinds.Failed, reason, now);
        }

        return affected;
    }

    public int CleanupCompletedBefore(long cutoffTimestamp)
    {
        return _db.ExecuteInTransaction((connection, transaction) =>
        {
            using var deleteLogs = connection.CreateCommand();
            deleteLogs.Transaction = transaction;
            deleteLogs.CommandText = """
                DELETE FROM DelegatedAgentJobLogs
                WHERE JobId IN (
                    SELECT Id
                    FROM DelegatedAgentJobs
                    WHERE State IN ('completed', 'failed', 'cancelled')
                      AND CompletedAt IS NOT NULL
                      AND CompletedAt < @Cutoff
                )
                """;
            deleteLogs.Parameters.AddWithValue("@Cutoff", cutoffTimestamp);
            deleteLogs.ExecuteNonQuery();

            using var deleteJobs = connection.CreateCommand();
            deleteJobs.Transaction = transaction;
            deleteJobs.CommandText = """
                DELETE FROM DelegatedAgentJobs
                WHERE State IN ('completed', 'failed', 'cancelled')
                  AND CompletedAt IS NOT NULL
                  AND CompletedAt < @Cutoff
                """;
            deleteJobs.Parameters.AddWithValue("@Cutoff", cutoffTimestamp);
            return deleteJobs.ExecuteNonQuery();
        });
    }

    private bool TryUpdateNonTerminal(
        string id,
        string state,
        string? progressDescription,
        string? resultJson,
        string? error,
        long? completedAt,
        string logKind,
        string logMessage)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var finalCompletedAt = completedAt ?? (TerminalStates.Contains(state) ? now : null);
        var affected = _db.Execute("""
            UPDATE DelegatedAgentJobs
            SET State = @State,
                ProgressDescription = COALESCE(@ProgressDescription, ProgressDescription),
                ResultJson = @ResultJson,
                Error = @Error,
                UpdatedAt = @Now,
                CompletedAt = @CompletedAt
            WHERE Id = @Id
              AND State NOT IN ('completed', 'failed', 'cancelled')
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@State", state);
                cmd.Parameters.AddWithValue("@ProgressDescription", (object?)progressDescription ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResultJson", (object?)resultJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@CompletedAt", (object?)finalCompletedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", id);
            });

        if (affected > 0)
        {
            AddLog(id, logKind, logMessage, now);
        }

        return affected > 0;
    }

    private void AddLog(string jobId, string kind, string message, long? now = null)
    {
        _db.Execute("""
            INSERT INTO DelegatedAgentJobLogs (JobId, Kind, Message, CreatedAt)
            VALUES (@JobId, @Kind, @Message, @CreatedAt)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@JobId", jobId);
                cmd.Parameters.AddWithValue("@Kind", kind);
                cmd.Parameters.AddWithValue("@Message", message);
                cmd.Parameters.AddWithValue("@CreatedAt", now ?? DateTimeOffset.Now.ToUnixTimeMilliseconds());
            });
    }

    private const string InsertJobSql = """
        INSERT INTO DelegatedAgentJobs (
            Id, ScopeKind, ScopeId, ParentTurnId, ParentAgentId, ChildName, ChildInstructions,
            TaskInputJson, ToolMountsJson, ProviderId, ModelId, State, ProgressDescription,
            ResultJson, Error, CreatedAt, UpdatedAt, CompletedAt
        ) VALUES (
            @Id, @ScopeKind, @ScopeId, @ParentTurnId, @ParentAgentId, @ChildName, @ChildInstructions,
            @TaskInputJson, @ToolMountsJson, @ProviderId, @ModelId, @State, @ProgressDescription,
            @ResultJson, @Error, @CreatedAt, @UpdatedAt, @CompletedAt
        )
        """;

    private static DelegatedAgentJobEntity ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        ScopeKind = reader.GetString(reader.GetOrdinal("ScopeKind")),
        ScopeId = reader.GetString(reader.GetOrdinal("ScopeId")),
        ParentTurnId = reader.GetString(reader.GetOrdinal("ParentTurnId")),
        ParentAgentId = reader.GetString(reader.GetOrdinal("ParentAgentId")),
        ChildName = reader.GetString(reader.GetOrdinal("ChildName")),
        ChildInstructions = reader.GetString(reader.GetOrdinal("ChildInstructions")),
        TaskInputJson = reader.GetString(reader.GetOrdinal("TaskInputJson")),
        ToolMountsJson = reader.GetString(reader.GetOrdinal("ToolMountsJson")),
        ProviderId = reader.IsDBNull(reader.GetOrdinal("ProviderId")) ? null : reader.GetString(reader.GetOrdinal("ProviderId")),
        ModelId = reader.IsDBNull(reader.GetOrdinal("ModelId")) ? null : reader.GetString(reader.GetOrdinal("ModelId")),
        State = reader.GetString(reader.GetOrdinal("State")),
        ProgressDescription = reader.IsDBNull(reader.GetOrdinal("ProgressDescription")) ? null : reader.GetString(reader.GetOrdinal("ProgressDescription")),
        ResultJson = reader.IsDBNull(reader.GetOrdinal("ResultJson")) ? null : reader.GetString(reader.GetOrdinal("ResultJson")),
        Error = reader.IsDBNull(reader.GetOrdinal("Error")) ? null : reader.GetString(reader.GetOrdinal("Error")),
        CreatedAt = reader.GetInt64(reader.GetOrdinal("CreatedAt")),
        UpdatedAt = reader.GetInt64(reader.GetOrdinal("UpdatedAt")),
        CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt")) ? null : reader.GetInt64(reader.GetOrdinal("CompletedAt")),
    };

    private static DelegatedAgentJobLogEntity ReadLog(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        JobId = reader.GetString(reader.GetOrdinal("JobId")),
        Kind = reader.GetString(reader.GetOrdinal("Kind")),
        Message = reader.GetString(reader.GetOrdinal("Message")),
        CreatedAt = reader.GetInt64(reader.GetOrdinal("CreatedAt")),
    };

    private static void BindJob(SqliteCommand cmd, DelegatedAgentJobEntity entity)
    {
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        cmd.Parameters.AddWithValue("@ScopeKind", entity.ScopeKind);
        cmd.Parameters.AddWithValue("@ScopeId", entity.ScopeId);
        cmd.Parameters.AddWithValue("@ParentTurnId", entity.ParentTurnId);
        cmd.Parameters.AddWithValue("@ParentAgentId", entity.ParentAgentId);
        cmd.Parameters.AddWithValue("@ChildName", entity.ChildName);
        cmd.Parameters.AddWithValue("@ChildInstructions", entity.ChildInstructions);
        cmd.Parameters.AddWithValue("@TaskInputJson", entity.TaskInputJson);
        cmd.Parameters.AddWithValue("@ToolMountsJson", entity.ToolMountsJson);
        cmd.Parameters.AddWithValue("@ProviderId", (object?)entity.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ModelId", (object?)entity.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@State", entity.State);
        cmd.Parameters.AddWithValue("@ProgressDescription", (object?)entity.ProgressDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResultJson", (object?)entity.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Error", (object?)entity.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", entity.UpdatedAt);
        cmd.Parameters.AddWithValue("@CompletedAt", (object?)entity.CompletedAt ?? DBNull.Value);
    }

    private static void InsertLog(SqliteConnection connection, string jobId, string kind, string message, long now)
    {
        using var log = connection.CreateCommand();
        log.CommandText = """
            INSERT INTO DelegatedAgentJobLogs (JobId, Kind, Message, CreatedAt)
            VALUES (@JobId, @Kind, @Message, @CreatedAt)
            """;
        log.Parameters.AddWithValue("@JobId", jobId);
        log.Parameters.AddWithValue("@Kind", kind);
        log.Parameters.AddWithValue("@Message", message);
        log.Parameters.AddWithValue("@CreatedAt", now);
        log.ExecuteNonQuery();
    }
}
