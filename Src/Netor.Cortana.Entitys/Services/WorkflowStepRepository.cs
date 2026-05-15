using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// Workflow 任务子表（Participant / Step / Message）数据服务。
    /// 阶段 2B 占位实现仅写 Participant + 不写 Step / Message；阶段 3B 起实际写入。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3.2 / §3.3 / §3.4。
    /// </summary>
    public sealed class WorkflowStepRepository(CortanaDbContext db)
    {
        private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

        // ──── Participant ────

        /// <summary>插入参与者。</summary>
        public void InsertParticipant(OrchestrationParticipantEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            _db.Execute("""
                INSERT INTO OrchestrationParticipant (TaskId, AgentId, AgentName, Role, JoinedAt)
                VALUES (@TaskId, @AgentId, @AgentName, @Role, @JoinedAt)
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@TaskId", entity.TaskId);
                    cmd.Parameters.AddWithValue("@AgentId", entity.AgentId);
                    cmd.Parameters.AddWithValue("@AgentName", entity.AgentName ?? string.Empty);
                    cmd.Parameters.AddWithValue("@Role", entity.Role ?? string.Empty);
                    cmd.Parameters.AddWithValue("@JoinedAt", entity.JoinedAt);
                });
        }

        /// <summary>批量插入参与者。</summary>
        public void InsertParticipants(IEnumerable<OrchestrationParticipantEntity> entities)
        {
            ArgumentNullException.ThrowIfNull(entities);
            foreach (var e in entities) InsertParticipant(e);
        }

        /// <summary>按任务 ID 查询所有参与者。</summary>
        public List<OrchestrationParticipantEntity> GetParticipantsByTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return [];
            return _db.Query(
                "SELECT * FROM OrchestrationParticipant WHERE TaskId = @TaskId ORDER BY Id ASC",
                ReadParticipant,
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        }

        // ──── Step ────

        /// <summary>插入步骤。</summary>
        public void InsertStep(OrchestrationStepEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            _db.Execute("""
                INSERT INTO OrchestrationStep
                    (Id, TaskId, ParentStepId, Sequence, AgentId, AgentName, Action, Status,
                     StartedAt, CompletedAt, DurationMs, TokenInputCount, TokenOutputCount,
                     ErrorMessage, SummaryJson)
                VALUES
                    (@Id, @TaskId, @ParentStepId, @Sequence, @AgentId, @AgentName, @Action, @Status,
                     @StartedAt, @CompletedAt, @DurationMs, @TokenInputCount, @TokenOutputCount,
                     @ErrorMessage, @SummaryJson)
                """,
                cmd => BindStep(cmd, entity));
        }

        /// <summary>更新步骤完成状态。</summary>
        public void UpdateStepCompleted(
            string stepId,
            string status,
            long completedAt,
            long durationMs,
            int? tokenInputCount,
            int? tokenOutputCount,
            string? errorMessage,
            string? summaryJson)
        {
            if (string.IsNullOrWhiteSpace(stepId))
                throw new ArgumentException("StepId cannot be null or empty.", nameof(stepId));

            _db.Execute("""
                UPDATE OrchestrationStep SET
                    Status = @Status,
                    CompletedAt = @CompletedAt,
                    DurationMs = @DurationMs,
                    TokenInputCount = @TokenInputCount,
                    TokenOutputCount = @TokenOutputCount,
                    ErrorMessage = @ErrorMessage,
                    SummaryJson = @SummaryJson
                WHERE Id = @Id
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Status", status);
                    cmd.Parameters.AddWithValue("@CompletedAt", completedAt);
                    cmd.Parameters.AddWithValue("@DurationMs", durationMs);
                    cmd.Parameters.AddWithValue("@TokenInputCount", (object?)tokenInputCount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@TokenOutputCount", (object?)tokenOutputCount ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SummaryJson", (object?)summaryJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Id", stepId);
                });
        }

        /// <summary>按任务 ID 查询所有步骤，按 Sequence 升序。</summary>
        public List<OrchestrationStepEntity> GetStepsByTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return [];
            return _db.Query(
                "SELECT * FROM OrchestrationStep WHERE TaskId = @TaskId ORDER BY Sequence ASC",
                ReadStep,
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        }

        // ──── Message ────

        /// <summary>插入消息。</summary>
        public void InsertMessage(OrchestrationMessageEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            _db.Execute("""
                INSERT INTO OrchestrationMessage
                    (TaskId, StepId, Sequence, Role, AuthorName, Content, CreatedAt, AttachmentsJson)
                VALUES
                    (@TaskId, @StepId, @Sequence, @Role, @AuthorName, @Content, @CreatedAt, @AttachmentsJson)
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@TaskId", entity.TaskId);
                    cmd.Parameters.AddWithValue("@StepId", entity.StepId);
                    cmd.Parameters.AddWithValue("@Sequence", entity.Sequence);
                    cmd.Parameters.AddWithValue("@Role", entity.Role ?? string.Empty);
                    cmd.Parameters.AddWithValue("@AuthorName", (object?)entity.AuthorName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Content", entity.Content ?? string.Empty);
                    cmd.Parameters.AddWithValue("@CreatedAt", entity.CreatedAt);
                    cmd.Parameters.AddWithValue("@AttachmentsJson", (object?)entity.AttachmentsJson ?? DBNull.Value);
                });
        }

        /// <summary>按任务 ID 查询所有消息，按 Sequence 升序。</summary>
        public List<OrchestrationMessageEntity> GetMessagesByTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return [];
            return _db.Query(
                "SELECT * FROM OrchestrationMessage WHERE TaskId = @TaskId ORDER BY Sequence ASC",
                ReadMessage,
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        }

        // ──── ReadEntity / BindEntity ────

        private static OrchestrationParticipantEntity ReadParticipant(SqliteDataReader r) => new()
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            TaskId = r.GetString(r.GetOrdinal("TaskId")),
            AgentId = r.GetString(r.GetOrdinal("AgentId")),
            AgentName = r.GetString(r.GetOrdinal("AgentName")),
            Role = r.GetString(r.GetOrdinal("Role")),
            JoinedAt = r.GetInt64(r.GetOrdinal("JoinedAt")),
        };

        private static OrchestrationStepEntity ReadStep(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            TaskId = r.GetString(r.GetOrdinal("TaskId")),
            ParentStepId = r.IsDBNull(r.GetOrdinal("ParentStepId")) ? null : r.GetString(r.GetOrdinal("ParentStepId")),
            Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
            AgentId = r.IsDBNull(r.GetOrdinal("AgentId")) ? null : r.GetString(r.GetOrdinal("AgentId")),
            AgentName = r.IsDBNull(r.GetOrdinal("AgentName")) ? null : r.GetString(r.GetOrdinal("AgentName")),
            Action = r.GetString(r.GetOrdinal("Action")),
            Status = r.GetString(r.GetOrdinal("Status")),
            StartedAt = r.GetInt64(r.GetOrdinal("StartedAt")),
            CompletedAt = r.IsDBNull(r.GetOrdinal("CompletedAt")) ? null : r.GetInt64(r.GetOrdinal("CompletedAt")),
            DurationMs = r.IsDBNull(r.GetOrdinal("DurationMs")) ? null : r.GetInt64(r.GetOrdinal("DurationMs")),
            TokenInputCount = r.IsDBNull(r.GetOrdinal("TokenInputCount")) ? null : r.GetInt32(r.GetOrdinal("TokenInputCount")),
            TokenOutputCount = r.IsDBNull(r.GetOrdinal("TokenOutputCount")) ? null : r.GetInt32(r.GetOrdinal("TokenOutputCount")),
            ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null : r.GetString(r.GetOrdinal("ErrorMessage")),
            SummaryJson = r.IsDBNull(r.GetOrdinal("SummaryJson")) ? null : r.GetString(r.GetOrdinal("SummaryJson")),
        };

        private static OrchestrationMessageEntity ReadMessage(SqliteDataReader r) => new()
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            TaskId = r.GetString(r.GetOrdinal("TaskId")),
            StepId = r.GetString(r.GetOrdinal("StepId")),
            Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
            Role = r.GetString(r.GetOrdinal("Role")),
            AuthorName = r.IsDBNull(r.GetOrdinal("AuthorName")) ? null : r.GetString(r.GetOrdinal("AuthorName")),
            Content = r.GetString(r.GetOrdinal("Content")),
            CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
            AttachmentsJson = r.IsDBNull(r.GetOrdinal("AttachmentsJson")) ? null : r.GetString(r.GetOrdinal("AttachmentsJson")),
        };

        private static void BindStep(SqliteCommand cmd, OrchestrationStepEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@TaskId", e.TaskId);
            cmd.Parameters.AddWithValue("@ParentStepId", (object?)e.ParentStepId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Sequence", e.Sequence);
            cmd.Parameters.AddWithValue("@AgentId", (object?)e.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AgentName", (object?)e.AgentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Action", e.Action ?? string.Empty);
            cmd.Parameters.AddWithValue("@Status", e.Status ?? string.Empty);
            cmd.Parameters.AddWithValue("@StartedAt", e.StartedAt);
            cmd.Parameters.AddWithValue("@CompletedAt", (object?)e.CompletedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationMs", (object?)e.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TokenInputCount", (object?)e.TokenInputCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TokenOutputCount", (object?)e.TokenOutputCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)e.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SummaryJson", (object?)e.SummaryJson ?? DBNull.Value);
        }
    }
}
