using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 会议模式会话数据服务（MeetingSessions 表）。
/// </summary>
public sealed class MeetingSessionService
{
    private readonly CortanaDbContext _db;

    public MeetingSessionService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>创建会议会话。</summary>
    public string Create(MeetingSessionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(entity.Id))
        {
            entity.Id = Guid.NewGuid().ToString("N");
        }

        entity.CreatedAt = now;
        entity.UpdatedAt = now;

        _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        return entity.Id;
    }

    /// <summary>根据 ID 获取会议。</summary>
    public MeetingSessionEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM MeetingSessions WHERE Id = @Id",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>查询当前会话的活跃会议。</summary>
    public MeetingSessionEntity? GetActiveBySession(string sessionId)
    {
        return _db.QueryFirstOrDefault("""
            SELECT * FROM MeetingSessions
            WHERE SessionId = @SessionId AND Status IN (0, 1)
            ORDER BY UpdatedAt DESC LIMIT 1
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>列出会话下的会议。</summary>
    public List<MeetingSessionEntity> ListBySession(string sessionId)
    {
        return _db.Query("""
            SELECT * FROM MeetingSessions
            WHERE SessionId = @SessionId
            ORDER BY CreatedAt DESC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>列出工作区下的会议。</summary>
    public List<MeetingSessionEntity> ListByWorkspace(string workspaceId)
    {
        return _db.Query("""
            SELECT * FROM MeetingSessions
            WHERE WorkspaceId = @WorkspaceId
            ORDER BY UpdatedAt DESC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId));
    }

    /// <summary>
    /// 创建会议专用支撑 ChatSession。
    /// 该记录仅满足 MeetingSessions 外键，不参与专家模式会话列表。
    /// </summary>
    public string CreateBackingChatSession(string workspaceId, string agentName)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var sessionId = Guid.NewGuid().ToString("N");
        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary,
                RawDiscription, AgentName, IsArchived, IsPinned, LastActiveTimestamp,
                TotalTokenCount, CompactedContext, CompactedAtCount
            ) VALUES (
                @Id, @Now, @Now, @WorkspaceId, @Title, '', '', @AgentName, 1, 0, @Now,
                0, '', 0
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", sessionId);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Title", "会议模式支撑会话");
                cmd.Parameters.AddWithValue("@AgentName", agentName);
            });

        return sessionId;
    }

    /// <summary>获取所有进行中或等待用户回复的会议。</summary>
    public List<MeetingSessionEntity> GetAllInProgress()
    {
        return _db.Query(
            "SELECT * FROM MeetingSessions WHERE Status IN (0, 1) ORDER BY UpdatedAt DESC",
            ReadEntity);
    }

    /// <summary>设置 RunId。</summary>
    public void SetRunId(string id, string runId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET RunId = @RunId, UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RunId", runId);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>设置会议状态。</summary>
    public void SetStatus(string id, int status)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET Status = @Status, UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>设置 HITL 挂起请求。</summary>
    public void SetPending(string id, string requestId, string kind, string snapshotJson)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET Status = 1,
                PendingRequestId = @RequestId,
                PendingRequestKind = @Kind,
                PendingRequestData = @Data,
                UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RequestId", requestId);
                cmd.Parameters.AddWithValue("@Kind", kind);
                cmd.Parameters.AddWithValue("@Data", snapshotJson);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>清除 HITL 挂起请求。</summary>
    public void ClearPending(string id)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET Status = 0,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>用 PendingRequestId 乐观锁清除挂起状态，避免重复 Resume。</summary>
    public int TryClearPending(string id, string expectedRequestId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return _db.Execute("""
            UPDATE MeetingSessions
            SET Status = 0,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now
            WHERE Id = @Id AND PendingRequestId = @ExpectedRequestId
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@ExpectedRequestId", expectedRequestId);
            });
    }

    /// <summary>写入最终总结并标记会议结束。</summary>
    public void SetFinalSummary(string id, string summaryMd)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET Status = 2,
                FinalSummaryMd = @Summary,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                EndedAt = @Now,
                UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Summary", summaryMd);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>标记会议已散会。</summary>
    public void MarkAsAdjourned(string id)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE MeetingSessions
            SET Status = 3,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                EndedAt = @Now,
                UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    private const string InsertSql = """
        INSERT INTO MeetingSessions (
            Id, SessionId, WorkspaceId, Topic, HostAgentId, ParticipantsJson, Status, RunId,
            PendingRequestId, PendingRequestKind, PendingRequestData, FinalSummaryMd,
            Provider, Model, CreatedAt, UpdatedAt, EndedAt
        ) VALUES (
            @Id, @SessionId, @WorkspaceId, @Topic, @HostAgentId, @ParticipantsJson, @Status, @RunId,
            @PendingRequestId, @PendingRequestKind, @PendingRequestData, @FinalSummaryMd,
            @Provider, @Model, @CreatedAt, @UpdatedAt, @EndedAt
        )
        """;

    private static MeetingSessionEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        SessionId = r.GetString(r.GetOrdinal("SessionId")),
        WorkspaceId = r.GetString(r.GetOrdinal("WorkspaceId")),
        Topic = r.GetString(r.GetOrdinal("Topic")),
        HostAgentId = r.GetString(r.GetOrdinal("HostAgentId")),
        ParticipantsJson = r.GetString(r.GetOrdinal("ParticipantsJson")),
        Status = r.GetInt32(r.GetOrdinal("Status")),
        RunId = ReadNullableString(r, "RunId"),
        PendingRequestId = ReadNullableString(r, "PendingRequestId"),
        PendingRequestKind = ReadNullableString(r, "PendingRequestKind"),
        PendingRequestData = ReadNullableString(r, "PendingRequestData"),
        FinalSummaryMd = ReadNullableString(r, "FinalSummaryMd"),
        Provider = r.GetString(r.GetOrdinal("Provider")),
        Model = r.GetString(r.GetOrdinal("Model")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
        EndedAt = ReadNullableInt64(r, "EndedAt"),
    };

    private static void BindEntity(SqliteCommand cmd, MeetingSessionEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@SessionId", e.SessionId);
        cmd.Parameters.AddWithValue("@WorkspaceId", e.WorkspaceId);
        cmd.Parameters.AddWithValue("@Topic", e.Topic);
        cmd.Parameters.AddWithValue("@HostAgentId", e.HostAgentId);
        cmd.Parameters.AddWithValue("@ParticipantsJson", e.ParticipantsJson);
        cmd.Parameters.AddWithValue("@Status", e.Status);
        cmd.Parameters.AddWithValue("@RunId", (object?)e.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestId", (object?)e.PendingRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestKind", (object?)e.PendingRequestKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestData", (object?)e.PendingRequestData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FinalSummaryMd", (object?)e.FinalSummaryMd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Provider", e.Provider);
        cmd.Parameters.AddWithValue("@Model", e.Model);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", e.UpdatedAt);
        cmd.Parameters.AddWithValue("@EndedAt", (object?)e.EndedAt ?? DBNull.Value);
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
