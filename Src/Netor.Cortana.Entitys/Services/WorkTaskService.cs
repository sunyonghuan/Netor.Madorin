using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 工作模式任务数据服务（WorkTasks 表）。
/// 提供任务 CRUD + 关键查询（活跃任务、孤儿任务、最近完成任务等）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §四。
/// </summary>
public sealed class WorkTaskService
{
    private readonly CortanaDbContext _db;

    public WorkTaskService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>根据 ID 获取任务。</summary>
    public WorkTaskEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM WorkTasks WHERE Id = @Id",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>查询当前会话的活跃任务（最多一个）。</summary>
    public WorkTaskEntity? GetActiveTask(string sessionId)
    {
        return _db.QueryFirstOrDefault("""
            SELECT * FROM WorkTasks
            WHERE SessionId = @SessionId AND IsActive = 1
            ORDER BY LastActiveAt DESC LIMIT 1
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>查询所有活跃任务（应用启动时检测孤儿用）。</summary>
    public List<WorkTaskEntity> GetAllActiveTasks()
    {
        return _db.Query(
            "SELECT * FROM WorkTasks WHERE IsActive = 1 ORDER BY LastActiveAt DESC",
            ReadEntity);
    }

    /// <summary>查询需要启动恢复决策的活跃任务，只包含正在执行或等待用户确认的执行轮次。</summary>
    public List<WorkTaskEntity> GetRecoverableActiveTasks()
    {
        return _db.Query("""
            SELECT * FROM WorkTasks
            WHERE IsActive = 1
              AND (
                  PendingRequestId IS NOT NULL
                  OR OrchestratorState = @RunningState
              )
            ORDER BY LastActiveAt DESC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@RunningState", WorkTaskOrchestratorStates.Running));
    }

    /// <summary>查询 B 层项目组长心跳超时的任务。</summary>
    public List<WorkTaskEntity> ListStaleRunningOrchestrators(long staleBefore)
    {
        return _db.Query("""
            SELECT * FROM WorkTasks
            WHERE IsActive = 1
              AND IsOrphaned = 0
              AND OrchestratorState = @State
              AND OrchestratorHeartbeatAt IS NOT NULL
              AND OrchestratorHeartbeatAt < @StaleBefore
            ORDER BY OrchestratorHeartbeatAt ASC
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@State", WorkTaskOrchestratorStates.Running);
                cmd.Parameters.AddWithValue("@StaleBefore", staleBefore);
            });
    }

    /// <summary>查询当前会话最近一次完成的任务（"再做一份"参照）。</summary>
    public WorkTaskEntity? GetMostRecentCompletedTask(string sessionId)
    {
        return _db.QueryFirstOrDefault("""
            SELECT * FROM WorkTasks
            WHERE SessionId = @SessionId AND CompletedAt IS NOT NULL
            ORDER BY CompletedAt DESC LIMIT 1
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>列出会话的所有任务（左侧任务列表用，按创建时间倒序）。</summary>
    public List<WorkTaskEntity> ListBySession(string sessionId, int take = 50)
    {
        return _db.Query("""
            SELECT * FROM WorkTasks WHERE SessionId = @SessionId
            ORDER BY CreatedAt DESC LIMIT @Take
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@Take", take);
            });
    }

    /// <summary>列出当前工作区的顶层工作任务（工作记录用，按最近活动时间倒序）。</summary>
    public List<WorkTaskEntity> ListByWorkspace(string workspaceId, int take = 80)
    {
        return _db.Query("""
            SELECT * FROM WorkTasks
            WHERE WorkspaceId = @WorkspaceId
              AND (ParentTaskId IS NULL OR ParentTaskId = '')
            ORDER BY LastActiveAt DESC, CreatedAt DESC LIMIT @Take
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Take", take);
            });
    }

    /// <summary>创建新任务。事务内检查"会话最多一个活跃任务"约束。</summary>
    public void Create(WorkTaskEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        entity.LastActiveAt = now;

        _db.ExecuteInTransaction(_ =>
        {
            // 单会话单活跃任务约束
            var existing = _db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM WorkTasks WHERE SessionId = @SessionId AND IsActive = 1",
                cmd => cmd.Parameters.AddWithValue("@SessionId", entity.SessionId));

            if (existing > 0)
                throw new InvalidOperationException(
                    $"Session {entity.SessionId} already has an active task; cannot create another.");

            _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        });
    }

    /// <summary>设置 RunId（任务启动后立即调用）。</summary>
    public void SetRunId(string taskId, string runId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET RunId = @RunId, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RunId", runId);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>设置 A 层总经理 RunId。</summary>
    public void SetManagerRunId(string taskId, string? managerRunId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET ManagerRunId = @RunId, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RunId", (object?)managerRunId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>更新 B 层项目组长状态，可选择同时刷新心跳。</summary>
    public void SetOrchestratorState(string taskId, string state, bool touchHeartbeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var heartbeat = touchHeartbeat ? now : (long?)null;
        _db.Execute("""
            UPDATE WorkTasks
            SET OrchestratorState = @State,
                OrchestratorHeartbeatAt = COALESCE(@Heartbeat, OrchestratorHeartbeatAt),
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@State", state.Trim());
                cmd.Parameters.AddWithValue("@Heartbeat", (object?)heartbeat ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>写入新计划时清空 B 层上一轮执行状态，工作记录和上一轮总结继续保留。</summary>
    public void ResetOrchestratorForNewPlan(string taskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks
            SET OrchestratorState = NULL,
                OrchestratorHeartbeatAt = NULL,
                ErrorMessage = NULL,
                HasPreemption = 0,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>刷新 B 层项目组长心跳。</summary>
    public void TouchOrchestrator(string taskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks
            SET OrchestratorHeartbeatAt = @Now,
                HeartbeatAt = @Now,
                LastActiveAt = @Now,
                UpdatedAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>更新计划。</summary>
    public void UpdatePlan(string taskId, string planJson)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET CurrentPlanJson = @Plan, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Plan", planJson);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>更新任务标题。</summary>
    public void UpdateTitle(string taskId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET Title = @Title, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Title", title.Trim());
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>设置来源任务（用于"再做一份"链路追踪）。</summary>
    public void SetSourceTask(string taskId, string? sourceTaskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET SourceTaskId = @SourceTaskId, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@SourceTaskId", (object?)sourceTaskId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>设置 / 清空 HITL 挂起请求。</summary>
    public void SetPendingRequest(string taskId, string? requestId, string? kind, string? data)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET PendingRequestId = @Id_, PendingRequestKind = @Kind, PendingRequestData = @Data,
                UpdatedAt = @Now, LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id_", (object?)requestId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Kind", (object?)kind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Data", (object?)data ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>用 PendingRequestId 乐观锁清除挂起请求，避免重复 Resume。</summary>
    public int TryClearPendingRequest(string taskId, string expectedRequestId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return _db.Execute("""
            UPDATE WorkTasks
            SET PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id AND PendingRequestId = @ExpectedRequestId
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
                cmd.Parameters.AddWithValue("@ExpectedRequestId", expectedRequestId);
            });
    }

    /// <summary>设置软抢占标志。</summary>
    public void SetPreemption(string taskId, bool flag)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET HasPreemption = @F, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@F", flag ? 1 : 0);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>更新心跳（由 WorkExecutionLogService.Append 内部调用）。</summary>
    public void Touch(string taskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET HeartbeatAt = @Now, LastActiveAt = @Now, UpdatedAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>标记为已完成。</summary>
    public void MarkCompleted(string taskId, string? finalReport)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET IsActive = 0, CompletedAt = @Now, FinalReport = @Report,
                OrchestratorState = @State, OrchestratorHeartbeatAt = @Now,
                PendingRequestId = NULL, PendingRequestKind = NULL, PendingRequestData = NULL,
                UpdatedAt = @Now, LastActiveAt = @Now
            WHERE Id = @Id
              AND IsActive = 1
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Report", (object?)finalReport ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@State", WorkTaskOrchestratorStates.Done);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>归档一轮执行完成结果，并释放 B 层执行器等待下一轮任务。</summary>
    public void RecordExecutionCompleted(string taskId, string? finalReport)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET CompletedAt = @Now,
                FinalReport = @Report,
                ErrorMessage = NULL,
                OrchestratorState = NULL,
                OrchestratorHeartbeatAt = NULL,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Report", (object?)finalReport ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>标记为失败。</summary>
    public void MarkFailed(string taskId, string errorMessage)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET IsActive = 0, CompletedAt = @Now, ErrorMessage = @Err,
                OrchestratorState = @State, OrchestratorHeartbeatAt = @Now,
                UpdatedAt = @Now, LastActiveAt = @Now
            WHERE Id = @Id
              AND IsActive = 1
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Err", errorMessage);
                cmd.Parameters.AddWithValue("@State", WorkTaskOrchestratorStates.Failed);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>归档一轮执行失败结果，并释放 B 层执行器等待 A 层调整后重新派发。</summary>
    public void RecordExecutionFailed(string taskId, string errorMessage)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET CompletedAt = @Now,
                ErrorMessage = @Err,
                OrchestratorState = NULL,
                OrchestratorHeartbeatAt = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Err", errorMessage);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>标记为已取消（用户主动）。</summary>
    public void MarkCancelled(string taskId, string? reason = null)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET IsActive = 0, CompletedAt = @Now, ErrorMessage = @Err,
                OrchestratorState = @State, OrchestratorHeartbeatAt = @Now,
                PendingRequestId = NULL, PendingRequestKind = NULL, PendingRequestData = NULL,
                UpdatedAt = @Now, LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Err", (object?)reason ?? "用户取消");
                cmd.Parameters.AddWithValue("@State", WorkTaskOrchestratorStates.Cancelled);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>归档一轮执行取消结果，并释放 B 层执行器等待下一轮任务。</summary>
    public void RecordExecutionCancelled(string taskId, string? reason = null)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET CompletedAt = @Now,
                ErrorMessage = @Err,
                OrchestratorState = NULL,
                OrchestratorHeartbeatAt = NULL,
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Err", (object?)reason ?? "用户取消");
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>关闭工作记录。只有用户显式结束当前工作或点击新建工作时使用。</summary>
    public void CloseTaskRecord(string taskId, string? reason = null)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            UPDATE WorkTasks SET IsActive = 0,
                CompletedAt = COALESCE(CompletedAt, @Now),
                ErrorMessage = COALESCE(@Reason, ErrorMessage),
                PendingRequestId = NULL,
                PendingRequestKind = NULL,
                PendingRequestData = NULL,
                UpdatedAt = @Now,
                LastActiveAt = @Now
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>应用启动时把活跃任务标记为孤儿（不自动恢复，等用户在 UI 决策）。</summary>
    public void MarkOrphaned(string taskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET IsOrphaned = 1, OrphanedDetectedAt = @Now, UpdatedAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    /// <summary>清除孤儿标记（用户在 UI 选"继续"后调用）。</summary>
    public void ClearOrphaned(string taskId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkTasks SET IsOrphaned = 0, OrphanedDetectedAt = NULL, UpdatedAt = @Now, LastActiveAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", taskId);
            });
    }

    private const string InsertSql = """
        INSERT INTO WorkTasks (
            Id, SessionId, WorkspaceId, Title, InitialInput, RunId, ManagerRunId, IsActive, CurrentPlanJson,
            PendingRequestId, PendingRequestKind, PendingRequestData,
            CompletedAt, FinalReport, ErrorMessage,
            Provider, Model, AgentName, MentionsJson, SourceTaskId, ParentTaskId,
            IsOrphaned, OrphanedDetectedAt, HeartbeatAt, OrchestratorState, OrchestratorHeartbeatAt, HasPreemption,
            CreatedAt, UpdatedAt, LastActiveAt
        ) VALUES (
            @Id, @SessionId, @WorkspaceId, @Title, @InitialInput, @RunId, @ManagerRunId, @IsActive, @CurrentPlanJson,
            @PendingRequestId, @PendingRequestKind, @PendingRequestData,
            @CompletedAt, @FinalReport, @ErrorMessage,
            @Provider, @Model, @AgentName, @MentionsJson, @SourceTaskId, @ParentTaskId,
            @IsOrphaned, @OrphanedDetectedAt, @HeartbeatAt, @OrchestratorState, @OrchestratorHeartbeatAt, @HasPreemption,
            @CreatedAt, @UpdatedAt, @LastActiveAt
        )
        """;

    private static WorkTaskEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        SessionId = r.GetString(r.GetOrdinal("SessionId")),
        WorkspaceId = r.GetString(r.GetOrdinal("WorkspaceId")),
        Title = r.GetString(r.GetOrdinal("Title")),
        InitialInput = r.GetString(r.GetOrdinal("InitialInput")),
        RunId = ReadNullableString(r, "RunId"),
        ManagerRunId = ReadNullableString(r, "ManagerRunId"),
        IsActive = r.GetInt32(r.GetOrdinal("IsActive")) != 0,
        CurrentPlanJson = ReadNullableString(r, "CurrentPlanJson"),
        PendingRequestId = ReadNullableString(r, "PendingRequestId"),
        PendingRequestKind = ReadNullableString(r, "PendingRequestKind"),
        PendingRequestData = ReadNullableString(r, "PendingRequestData"),
        CompletedAt = ReadNullableInt64(r, "CompletedAt"),
        FinalReport = ReadNullableString(r, "FinalReport"),
        ErrorMessage = ReadNullableString(r, "ErrorMessage"),
        Provider = r.GetString(r.GetOrdinal("Provider")),
        Model = r.GetString(r.GetOrdinal("Model")),
        AgentName = r.GetString(r.GetOrdinal("AgentName")),
        MentionsJson = ReadNullableString(r, "MentionsJson"),
        SourceTaskId = ReadNullableString(r, "SourceTaskId"),
        ParentTaskId = ReadNullableString(r, "ParentTaskId"),
        IsOrphaned = r.GetInt32(r.GetOrdinal("IsOrphaned")) != 0,
        OrphanedDetectedAt = ReadNullableInt64(r, "OrphanedDetectedAt"),
        HeartbeatAt = ReadNullableInt64(r, "HeartbeatAt"),
        OrchestratorState = ReadNullableString(r, "OrchestratorState"),
        OrchestratorHeartbeatAt = ReadNullableInt64(r, "OrchestratorHeartbeatAt"),
        HasPreemption = r.GetInt32(r.GetOrdinal("HasPreemption")) != 0,
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
        LastActiveAt = r.GetInt64(r.GetOrdinal("LastActiveAt")),
    };

    private static void BindEntity(SqliteCommand cmd, WorkTaskEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@SessionId", e.SessionId);
        cmd.Parameters.AddWithValue("@WorkspaceId", e.WorkspaceId);
        cmd.Parameters.AddWithValue("@Title", e.Title);
        cmd.Parameters.AddWithValue("@InitialInput", e.InitialInput);
        cmd.Parameters.AddWithValue("@RunId", (object?)e.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ManagerRunId", (object?)e.ManagerRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsActive", e.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@CurrentPlanJson", (object?)e.CurrentPlanJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestId", (object?)e.PendingRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestKind", (object?)e.PendingRequestKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingRequestData", (object?)e.PendingRequestData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAt", (object?)e.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FinalReport", (object?)e.FinalReport ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)e.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Provider", e.Provider);
        cmd.Parameters.AddWithValue("@Model", e.Model);
        cmd.Parameters.AddWithValue("@AgentName", e.AgentName);
        cmd.Parameters.AddWithValue("@MentionsJson", (object?)e.MentionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceTaskId", (object?)e.SourceTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ParentTaskId", (object?)e.ParentTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsOrphaned", e.IsOrphaned ? 1 : 0);
        cmd.Parameters.AddWithValue("@OrphanedDetectedAt", (object?)e.OrphanedDetectedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HeartbeatAt", (object?)e.HeartbeatAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OrchestratorState", (object?)e.OrchestratorState ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OrchestratorHeartbeatAt", (object?)e.OrchestratorHeartbeatAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HasPreemption", e.HasPreemption ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", e.UpdatedAt);
        cmd.Parameters.AddWithValue("@LastActiveAt", e.LastActiveAt);
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
