using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Persists work sessions, plans, steps, attempts, and background jobs.</summary>
public sealed class SqliteWorkRepository : IWorkRecoveryStore
{
    private readonly SqliteConnection _connection;

    public SqliteWorkRepository(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task UpsertSessionAsync(
        string sessionId,
        string runId,
        WorkSessionStatus status,
        string generalManagerId,
        WorkflowPolicy policy,
        WorkContextPolicy contextPolicy,
        string planVersion,
        string? planMessageId,
        WorkPlanDraft? plan,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generalManagerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contextPolicy);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        var policyJson = JsonSerializer.Serialize(policy, RuntimeJsonContext.Default.WorkflowPolicy);
        var contextJson = JsonSerializer.Serialize(contextPolicy, RuntimeJsonContext.Default.WorkContextPolicy);
        var planJson = plan is null
            ? null
            : JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_sessions(
                session_id, run_id, status, general_manager_id,
                workflow_policy_json, context_policy_json, plan_version,
                plan_message_id, plan_json, created_at, updated_at)
            VALUES(
                $sessionId, $runId, $status, $gm,
                $policyJson, $contextJson, $planVersion,
                $planMessageId, $planJson, $now, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                run_id = excluded.run_id,
                status = excluded.status,
                general_manager_id = excluded.general_manager_id,
                workflow_policy_json = excluded.workflow_policy_json,
                context_policy_json = excluded.context_policy_json,
                plan_version = excluded.plan_version,
                plan_message_id = excluded.plan_message_id,
                plan_json = excluded.plan_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$status", ToDbStatus(status));
        command.Parameters.AddWithValue("$gm", generalManagerId);
        command.Parameters.AddWithValue("$policyJson", policyJson);
        command.Parameters.AddWithValue("$contextJson", contextJson);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$planMessageId", (object?)planMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$planJson", (object?)planJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSessionStatusAsync(
        string sessionId,
        WorkSessionStatus status,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_sessions
            SET status = $status,
                updated_at = $now
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$status", ToDbStatus(status));
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkRequiresManualInterventionAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET requires_manual_intervention = 1,
                status = CASE
                    WHEN status IN ('completed', 'failed', 'cancelled') THEN status
                    ELSE 'interrupted'
                END,
                updated_at = $now
            WHERE session_id = $sessionId;
            """;
        sessionCommand.Parameters.AddWithValue("$now", now);
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = CASE
                    WHEN status IN ('completed', 'failed', 'skipped') THEN status
                    ELSE 'interrupted'
                END,
                error_message = COALESCE(error_message, $reason)
            WHERE session_id = $sessionId
              AND step_id = $workStepId
              AND plan_version = $planVersion;
            """;
        stepCommand.Parameters.AddWithValue("$reason", reason);
        stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
        stepCommand.Parameters.AddWithValue("$workStepId", workStepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task SavePlanStepsAsync(
        string sessionId,
        string runId,
        WorkPlanDraft plan,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(plan);

        var createdAt = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            var inputs = SerializeStepInputs(step);
            var hash = ComputeStepInputHash(step.StepId, plan.PlanVersion, inputs);
            var dependsJson = JsonSerializer.Serialize(
                step.DependsOn ?? [],
                RuntimeJsonContext.Default.StringArray);

            await using var stepCommand = _connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText = """
                INSERT INTO work_steps(
                    step_id, plan_version, run_id, session_id, target_agent_id,
                    status, step_input_hash, created_at, parent_step_id, depth,
                    step_index, attempt_count, title, goal, is_background, depends_json)
                VALUES(
                    $stepId, $planVersion, $runId, $sessionId, $agentId,
                    $status, $hash, $createdAt, $parent, $depth,
                    $index, 0, $title, $goal, $background, $depends)
                ON CONFLICT(step_id, plan_version) DO NOTHING;
                """;
            stepCommand.Parameters.AddWithValue("$stepId", step.StepId);
            stepCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
            stepCommand.Parameters.AddWithValue("$agentId", step.TargetAgentId);
            stepCommand.Parameters.AddWithValue("$status", "pending");
            stepCommand.Parameters.AddWithValue("$hash", hash);
            stepCommand.Parameters.AddWithValue("$createdAt", createdAt);
            stepCommand.Parameters.AddWithValue("$parent", (object?)step.ParentStepId ?? DBNull.Value);
            stepCommand.Parameters.AddWithValue("$depth", step.Depth);
            stepCommand.Parameters.AddWithValue("$index", index);
            stepCommand.Parameters.AddWithValue("$title", (object?)step.Title ?? DBNull.Value);
            stepCommand.Parameters.AddWithValue("$goal", step.Goal);
            stepCommand.Parameters.AddWithValue("$background", step.IsBackground ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$depends", dependsJson);
            await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var dep in step.DependsOn ?? [])
            {
                await using var depCommand = _connection.CreateCommand();
                depCommand.Transaction = transaction;
                depCommand.CommandText = """
                    INSERT OR IGNORE INTO work_step_dependencies(
                        step_id, plan_version, depends_on_step_id)
                    VALUES($stepId, $planVersion, $dep);
                    """;
                depCommand.Parameters.AddWithValue("$stepId", step.StepId);
                depCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
                depCommand.Parameters.AddWithValue("$dep", dep);
                await depCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<WorkStepSnapshot>> ListStepsAsync(
        string sessionId,
        string? planVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = _connection.CreateCommand();
        command.CommandText = planVersion is null
            ? """
              SELECT step_id, plan_version, session_id, run_id, target_agent_id, status,
                     step_input_hash, depth, parent_step_id, invocation_id, result_json,
                     error_message, attempt_count, depends_json, started_at, completed_at, goal
              FROM work_steps
              WHERE session_id = $sessionId
              ORDER BY step_index, step_id;
              """
            : """
              SELECT step_id, plan_version, session_id, run_id, target_agent_id, status,
                     step_input_hash, depth, parent_step_id, invocation_id, result_json,
                     error_message, attempt_count, depends_json, started_at, completed_at, goal
              FROM work_steps
              WHERE session_id = $sessionId AND plan_version = $planVersion
              ORDER BY step_index, step_id;
              """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        if (planVersion is not null)
        {
            command.Parameters.AddWithValue("$planVersion", planVersion);
        }

        var list = new List<WorkStepSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadStep(reader));
        }

        return list;
    }

    public async Task ResetStepForRetryAsync(
        string stepId,
        string planVersion,
        string errorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_steps
            SET status = 'pending',
                invocation_id = NULL,
                error_message = $errorMessage,
                result_json = NULL,
                started_at = NULL,
                completed_at = NULL
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND status = 'failed';
            """;
        command.Parameters.AddWithValue("$errorMessage", errorMessage);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Work step '{stepId}' cannot be reset for retry because it is not failed.");
        }
    }

    public async Task<WorkResumeState?> GetResumeStateAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT status, general_manager_id, plan_version, plan_message_id,
                   requires_manual_intervention, pending_approval_request_id
            FROM work_sessions
            WHERE session_id = $sessionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var status = ParseSessionStatus(reader.GetString(0));
        var gm = reader.GetString(1);
        var planVersion = reader.GetString(2);
        var planMessageId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var requiresManual = reader.GetInt64(4) != 0;
        var pendingApproval = reader.IsDBNull(5) ? null : reader.GetString(5);
        await reader.DisposeAsync().ConfigureAwait(false);

        var steps = await ListStepsAsync(sessionId, planVersion, ct).ConfigureAwait(false);
        var current = steps.FirstOrDefault(static s =>
            s.Status is WorkStepLifecycleStatus.Running
                or WorkStepLifecycleStatus.WaitingForApproval
                or WorkStepLifecycleStatus.Pending);

        var sentCallIds = await ListSentToolCallIdsAsync(sessionId, ct).ConfigureAwait(false);
        return new WorkResumeState(
            status,
            gm,
            planVersion,
            planMessageId,
            [.. steps],
            current,
            BackgroundJobs: null,
            SentToolCallIds: sentCallIds,
            RequiresManualIntervention: requiresManual,
            PendingApprovalRequestId: pendingApproval);
    }

    private async Task<string[]> ListSentToolCallIdsAsync(string sessionId, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT call_id
            FROM tool_intents
            WHERE session_id = $sessionId
              AND status = 'sent'
            ORDER BY created_at, call_id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids];
    }

    private static WorkStepSnapshot ReadStep(SqliteDataReader reader)
    {
        var dependsJson = reader.IsDBNull(13) ? null : reader.GetString(13);
        string[]? depends = null;
        if (dependsJson is not null)
        {
            depends = JsonSerializer.Deserialize(dependsJson, RuntimeJsonContext.Default.StringArray);
        }

        return new WorkStepSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            ParseStepStatus(reader.IsDBNull(5) ? "pending" : reader.GetString(5)),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetInt64(7), CultureInfo.InvariantCulture),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetInt64(12), CultureInfo.InvariantCulture),
            depends,
            ParseTimestamp(reader.IsDBNull(14) ? null : reader.GetString(14)),
            ParseTimestamp(reader.IsDBNull(15) ? null : reader.GetString(15)));
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDbStatus(WorkSessionStatus status) => status switch
    {
        WorkSessionStatus.Planning => "planning",
        WorkSessionStatus.Executing => "executing",
        WorkSessionStatus.Paused => "paused",
        WorkSessionStatus.WaitingForApproval => "waiting_for_approval",
        WorkSessionStatus.Completed => "completed",
        WorkSessionStatus.Failed => "failed",
        WorkSessionStatus.Cancelled => "cancelled",
        WorkSessionStatus.Interrupted => "interrupted",
        _ => status.ToString().ToLowerInvariant()
    };

    private static WorkSessionStatus ParseSessionStatus(string value) => value.ToLowerInvariant() switch
    {
        "planning" => WorkSessionStatus.Planning,
        "executing" => WorkSessionStatus.Executing,
        "paused" => WorkSessionStatus.Paused,
        "waiting_for_approval" => WorkSessionStatus.WaitingForApproval,
        "completed" => WorkSessionStatus.Completed,
        "failed" => WorkSessionStatus.Failed,
        "cancelled" => WorkSessionStatus.Cancelled,
        "interrupted" => WorkSessionStatus.Interrupted,
        _ => Enum.Parse<WorkSessionStatus>(value, ignoreCase: true)
    };

    private static WorkStepLifecycleStatus ParseStepStatus(string value) => value.ToLowerInvariant() switch
    {
        "pending" => WorkStepLifecycleStatus.Pending,
        "running" => WorkStepLifecycleStatus.Running,
        "completed" => WorkStepLifecycleStatus.Completed,
        "failed" => WorkStepLifecycleStatus.Failed,
        "interrupted" => WorkStepLifecycleStatus.Interrupted,
        "skipped" => WorkStepLifecycleStatus.Skipped,
        "waiting_for_approval" => WorkStepLifecycleStatus.WaitingForApproval,
        _ => Enum.Parse<WorkStepLifecycleStatus>(value, ignoreCase: true)
    };

    private static string SerializeStepInputs(WorkPlanStepDraft step)
    {
        if (step.Inputs is { } inputs
            && inputs.ValueKind is not JsonValueKind.Undefined
            and not JsonValueKind.Null)
        {
            return inputs.GetRawText();
        }

        return string.Join(
            "\n",
            step.Goal,
            step.Title ?? string.Empty,
            step.TargetAgentId,
            step.Depth.ToString(CultureInfo.InvariantCulture),
            step.ParentStepId ?? string.Empty);
    }

    private static string ComputeStepInputHash(
        string stepId,
        string planVersion,
        string serializedStepInputs)
    {
        var material = $"{stepId}\n{planVersion}\n{serializedStepInputs}";
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
