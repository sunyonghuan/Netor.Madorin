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

    public async Task MarkSessionInterruptedAsync(
        string sessionId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var attemptCommand = _connection.CreateCommand();
        attemptCommand.Transaction = transaction;
        attemptCommand.CommandText = """
            UPDATE work_step_attempts
            SET status = 'interrupted',
                error_message = $reason,
                completed_at = $now
            WHERE status IN ('running', 'waiting_for_approval', 'waiting_for_credentials')
              AND EXISTS (
                  SELECT 1
                  FROM work_steps s
                  WHERE s.session_id = $sessionId
                    AND s.step_id = work_step_attempts.step_id
                    AND s.plan_version = work_step_attempts.plan_version
                    AND s.invocation_id = work_step_attempts.invocation_id);
            """;
        attemptCommand.Parameters.AddWithValue("$sessionId", sessionId);
        attemptCommand.Parameters.AddWithValue("$reason", reason);
        attemptCommand.Parameters.AddWithValue("$now", now);
        await attemptCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = 'interrupted',
                error_message = $reason,
                completed_at = $now
            WHERE session_id = $sessionId
              AND status IN ('running', 'waiting_for_approval', 'waiting_for_credentials');
            """;
        stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
        stepCommand.Parameters.AddWithValue("$reason", reason);
        stepCommand.Parameters.AddWithValue("$now", now);
        await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var jobCommand = _connection.CreateCommand();
        jobCommand.Transaction = transaction;
        jobCommand.CommandText = """
            UPDATE work_background_jobs
            SET status = 'cancelled',
                error_message = $reason,
                completed_at = $now
            WHERE session_id = $sessionId
              AND status IN ('pending', 'running');
            """;
        jobCommand.Parameters.AddWithValue("$sessionId", sessionId);
        jobCommand.Parameters.AddWithValue("$reason", reason);
        jobCommand.Parameters.AddWithValue("$now", now);
        await jobCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET status = 'interrupted',
                pending_approval_request_id = NULL,
                pending_approval_json = NULL,
                updated_at = $now
            WHERE session_id = $sessionId
              AND status NOT IN ('completed', 'failed', 'interrupted');
            """;
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        sessionCommand.Parameters.AddWithValue("$now", now);
        await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task MarkStepWaitingForApprovalAsync(
        string sessionId,
        string stepId,
        string planVersion,
        string approvalRequestId,
        string? approvalJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET status = 'waiting_for_approval',
                pending_approval_request_id = $approvalRequestId,
                pending_approval_json = $approvalJson,
                updated_at = $now
            WHERE session_id = $sessionId
              AND status NOT IN ('completed', 'failed', 'cancelled');
            """;
        sessionCommand.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
        sessionCommand.Parameters.AddWithValue("$approvalJson", (object?)approvalJson ?? DBNull.Value);
        sessionCommand.Parameters.AddWithValue("$now", now);
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = 'waiting_for_approval',
                error_message = NULL
            WHERE session_id = $sessionId
              AND step_id = $stepId
              AND plan_version = $planVersion
              AND status IN ('running', 'waiting_for_approval');
            """;
        stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
        stepCommand.Parameters.AddWithValue("$stepId", stepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        var updatedSteps = await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (updatedSteps != 1)
        {
            throw new InvalidOperationException(
                $"Work step '{stepId}' cannot enter approval wait because it is not running.");
        }

        await UpdateLatestStepAttemptStatusAsync(
            stepId,
            planVersion,
            "waiting_for_approval",
            completedAt: null,
            transaction,
            ct).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task MarkStepRunningAfterApprovalAsync(
        string sessionId,
        string stepId,
        string planVersion,
        string approvalRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET status = 'executing',
                pending_approval_request_id = NULL,
                pending_approval_json = NULL,
                updated_at = $now
            WHERE session_id = $sessionId
              AND pending_approval_request_id = $approvalRequestId
              AND status = 'waiting_for_approval';
            """;
        sessionCommand.Parameters.AddWithValue("$now", now);
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        sessionCommand.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
        await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = 'running'
            WHERE session_id = $sessionId
              AND step_id = $stepId
              AND plan_version = $planVersion
              AND status = 'waiting_for_approval';
            """;
        stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
        stepCommand.Parameters.AddWithValue("$stepId", stepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        var updatedSteps = await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (updatedSteps != 1)
        {
            throw new InvalidOperationException(
                $"Work step '{stepId}' cannot leave approval wait because it is not waiting.");
        }

        await UpdateLatestStepAttemptStatusAsync(
            stepId,
            planVersion,
            "running",
            completedAt: null,
            transaction,
            ct).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task MarkCredentialsWaitAsync(
        string sessionId,
        string? stepId,
        string? planVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateOptionalStepIdentity(stepId, planVersion);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET status = 'waiting_for_credentials',
                updated_at = $now
            WHERE session_id = $sessionId
              AND status IN ('planning', 'executing', 'waiting_for_credentials');
            """;
        sessionCommand.Parameters.AddWithValue("$now", now);
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        if (await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Work session '{sessionId}' cannot enter credential wait from its current state.");
        }

        if (stepId is not null)
        {
            await using var stepCommand = _connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText = """
                UPDATE work_steps
                SET status = 'waiting_for_credentials',
                    error_message = NULL
                WHERE session_id = $sessionId
                  AND step_id = $stepId
                  AND plan_version = $planVersion
                  AND status IN ('running', 'waiting_for_credentials');
                """;
            stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
            stepCommand.Parameters.AddWithValue("$stepId", stepId);
            stepCommand.Parameters.AddWithValue("$planVersion", planVersion!);
            if (await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Work step '{stepId}' cannot enter credential wait because it is not running.");
            }

            await UpdateLatestStepAttemptStatusAsync(
                stepId,
                planVersion!,
                "waiting_for_credentials",
                completedAt: null,
                transaction,
                ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task MarkCredentialsWaitEndedAsync(
        string sessionId,
        string? stepId,
        string? planVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateOptionalStepIdentity(stepId, planVersion);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var sessionCommand = _connection.CreateCommand();
        sessionCommand.Transaction = transaction;
        sessionCommand.CommandText = """
            UPDATE work_sessions
            SET status = $status,
                updated_at = $now
            WHERE session_id = $sessionId
              AND status = 'waiting_for_credentials';
            """;
        sessionCommand.Parameters.AddWithValue(
            "$status",
            stepId is null ? "planning" : "executing");
        sessionCommand.Parameters.AddWithValue("$now", now);
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        if (await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Work session '{sessionId}' cannot leave credential wait from its current state.");
        }

        if (stepId is not null)
        {
            await using var stepCommand = _connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText = """
                UPDATE work_steps
                SET status = 'running'
                WHERE session_id = $sessionId
                  AND step_id = $stepId
                  AND plan_version = $planVersion
                  AND status = 'waiting_for_credentials';
                """;
            stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
            stepCommand.Parameters.AddWithValue("$stepId", stepId);
            stepCommand.Parameters.AddWithValue("$planVersion", planVersion!);
            if (await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Work step '{stepId}' cannot leave credential wait because it is not waiting.");
            }

            await UpdateLatestStepAttemptStatusAsync(
                stepId,
                planVersion!,
                "running",
                completedAt: null,
                transaction,
                ct).ConfigureAwait(false);
        }

        transaction.Commit();
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

        using var transaction = _connection.BeginTransaction(deferred: false);
        await InsertPlanStepsAsync(
            sessionId,
            runId,
            plan,
            stepMessageIds: null,
            transaction,
            ct).ConfigureAwait(false);

        transaction.Commit();
    }

    /// <summary>
    /// Atomically publishes one immutable plan revision and all of its structural steps.
    /// Canonical messages must be appended before this method is called.
    /// </summary>
    public async Task SavePlanRevisionAsync(
        string sessionId,
        string runId,
        string generalManagerId,
        WorkflowPolicy policy,
        WorkContextPolicy contextPolicy,
        WorkPlanDraft plan,
        string planMessageId,
        IReadOnlyDictionary<string, string> stepMessageIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generalManagerId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contextPolicy);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(planMessageId);
        ArgumentNullException.ThrowIfNull(stepMessageIds);

        if (stepMessageIds.Count != plan.Steps.Length
            || plan.Steps.Any(step => !stepMessageIds.ContainsKey(step.StepId)))
        {
            throw new ArgumentException(
                "Every plan step must reference exactly one CanonicalHistory message.",
                nameof(stepMessageIds));
        }

        var createdAt = FormatTimestamp(DateTimeOffset.UtcNow);
        var policyJson = JsonSerializer.Serialize(policy, RuntimeJsonContext.Default.WorkflowPolicy);
        var contextJson = JsonSerializer.Serialize(
            contextPolicy,
            RuntimeJsonContext.Default.WorkContextPolicy);
        var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
        using var transaction = _connection.BeginTransaction(deferred: false);

        var currentPlanVersion = await GetCurrentPlanVersionAsync(sessionId, transaction, ct)
            .ConfigureAwait(false);
        await ValidateRevisionSequenceAsync(
            sessionId,
            currentPlanVersion,
            plan,
            transaction,
            ct).ConfigureAwait(false);
        await ValidateStepRevisionLinksAsync(sessionId, plan, transaction, ct)
            .ConfigureAwait(false);

        await using (var sessionCommand = _connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = """
                INSERT INTO work_sessions(
                    session_id, run_id, status, general_manager_id,
                    workflow_policy_json, context_policy_json, plan_version,
                    plan_message_id, plan_json, created_at, updated_at)
                VALUES(
                    $sessionId, $runId, 'executing', $gm,
                    $policyJson, $contextJson, $planVersion,
                    $planMessageId, $planJson, $now, $now)
                ON CONFLICT(session_id) DO UPDATE SET
                    run_id = excluded.run_id,
                    status = 'executing',
                    general_manager_id = excluded.general_manager_id,
                    workflow_policy_json = excluded.workflow_policy_json,
                    context_policy_json = excluded.context_policy_json,
                    plan_version = excluded.plan_version,
                    plan_message_id = excluded.plan_message_id,
                    plan_json = excluded.plan_json,
                    updated_at = excluded.updated_at;
                """;
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            sessionCommand.Parameters.AddWithValue("$runId", runId);
            sessionCommand.Parameters.AddWithValue("$gm", generalManagerId);
            sessionCommand.Parameters.AddWithValue("$policyJson", policyJson);
            sessionCommand.Parameters.AddWithValue("$contextJson", contextJson);
            sessionCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
            sessionCommand.Parameters.AddWithValue("$planMessageId", planMessageId);
            sessionCommand.Parameters.AddWithValue("$planJson", planJson);
            sessionCommand.Parameters.AddWithValue("$now", createdAt);
            await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var revisionCommand = _connection.CreateCommand())
        {
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText = """
                INSERT INTO work_plan_revisions(
                    session_id, plan_version, run_id, plan_message_id,
                    plan_json, previous_plan_version, created_at)
                VALUES(
                    $sessionId, $planVersion, $runId, $planMessageId,
                    $planJson, $previousPlanVersion, $createdAt);
                """;
            revisionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            revisionCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
            revisionCommand.Parameters.AddWithValue("$runId", runId);
            revisionCommand.Parameters.AddWithValue("$planMessageId", planMessageId);
            revisionCommand.Parameters.AddWithValue("$planJson", planJson);
            revisionCommand.Parameters.AddWithValue(
                "$previousPlanVersion",
                (object?)plan.PreviousPlanVersion ?? DBNull.Value);
            revisionCommand.Parameters.AddWithValue("$createdAt", createdAt);
            await revisionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await InsertPlanStepsAsync(
            sessionId,
            runId,
            plan,
            stepMessageIds,
            transaction,
            ct).ConfigureAwait(false);
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
                     error_message, attempt_count, depends_json, started_at, completed_at, goal,
                     step_message_id, reuses_step_id, replaces_step_id, checkpoint_json
              FROM work_steps
              WHERE session_id = $sessionId
              ORDER BY step_index, step_id;
              """
            : """
              SELECT step_id, plan_version, session_id, run_id, target_agent_id, status,
                     step_input_hash, depth, parent_step_id, invocation_id, result_json,
                     error_message, attempt_count, depends_json, started_at, completed_at, goal,
                     step_message_id, reuses_step_id, replaces_step_id, checkpoint_json
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

    public async Task<WorkBackgroundJobSnapshot> CreateBackgroundJobAsync(
        string jobId,
        string sessionId,
        string runId,
        string stepId,
        string planVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO work_background_jobs(
                job_id, session_id, run_id, step_id, plan_version, status, created_at)
            VALUES(
                $jobId, $sessionId, $runId, $stepId, $planVersion, $status, $createdAt);
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$status", ToDbStatus(WorkBackgroundJobStatus.Pending));
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var job = await GetBackgroundJobAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Background job '{jobId}' could not be created.");
        EnsureSameBackgroundJob(job, sessionId, runId, stepId, planVersion);
        return job;
    }

    public async Task<bool> TryStartBackgroundJobAsync(
        string jobId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var startedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET status = $running,
                error_message = NULL,
                started_at = $startedAt,
                completed_at = NULL
            WHERE job_id = $jobId
              AND status = $pending;
            """;
        command.Parameters.AddWithValue("$running", ToDbStatus(WorkBackgroundJobStatus.Running));
        command.Parameters.AddWithValue("$startedAt", startedAt);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$pending", ToDbStatus(WorkBackgroundJobStatus.Pending));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task CompleteBackgroundJobAsync(
        string jobId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var completedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET status = $completed,
                error_message = NULL,
                completed_at = $completedAt
            WHERE job_id = $jobId
              AND status = $running;
            """;
        command.Parameters.AddWithValue("$completed", ToDbStatus(WorkBackgroundJobStatus.Completed));
        command.Parameters.AddWithValue("$completedAt", completedAt);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$running", ToDbStatus(WorkBackgroundJobStatus.Running));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Background job '{jobId}' is missing or is no longer running.");
        }
    }

    public async Task FailBackgroundJobAsync(
        string jobId,
        string errorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var completedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET status = $failed,
                error_message = $errorMessage,
                completed_at = $completedAt
            WHERE job_id = $jobId
              AND status IN ($pending, $running);
            """;
        command.Parameters.AddWithValue("$failed", ToDbStatus(WorkBackgroundJobStatus.Failed));
        command.Parameters.AddWithValue("$errorMessage", errorMessage);
        command.Parameters.AddWithValue("$completedAt", completedAt);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$pending", ToDbStatus(WorkBackgroundJobStatus.Pending));
        command.Parameters.AddWithValue("$running", ToDbStatus(WorkBackgroundJobStatus.Running));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Background job '{jobId}' is missing or is already terminal.");
        }
    }

    public async Task<bool> CancelBackgroundJobAsync(
        string jobId,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var completedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET status = $cancelled,
                error_message = $reason,
                completed_at = $completedAt
            WHERE job_id = $jobId
              AND status IN ($pending, $running);
            """;
        command.Parameters.AddWithValue("$cancelled", ToDbStatus(WorkBackgroundJobStatus.Cancelled));
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", completedAt);
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$pending", ToDbStatus(WorkBackgroundJobStatus.Pending));
        command.Parameters.AddWithValue("$running", ToDbStatus(WorkBackgroundJobStatus.Running));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<int> CancelTimedOutBackgroundJobsAsync(
        TimeSpan timeout,
        string reason,
        CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var completedAt = DateTimeOffset.UtcNow;
        var cutoff = completedAt.Subtract(timeout);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET status = $cancelled,
                error_message = $reason,
                completed_at = $completedAt
            WHERE status IN ($pending, $running)
              AND COALESCE(started_at, created_at) <= $cutoff;
            """;
        command.Parameters.AddWithValue("$cancelled", ToDbStatus(WorkBackgroundJobStatus.Cancelled));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completedAt));
        command.Parameters.AddWithValue("$pending", ToDbStatus(WorkBackgroundJobStatus.Pending));
        command.Parameters.AddWithValue("$running", ToDbStatus(WorkBackgroundJobStatus.Running));
        command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<WorkBackgroundJobSnapshot?> GetBackgroundJobAsync(
        string jobId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, session_id, run_id, step_id, plan_version, status,
                   created_at, started_at, completed_at, error_message
            FROM work_background_jobs
            WHERE job_id = $jobId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadBackgroundJob(reader)
            : null;
    }

    public async Task<IReadOnlyList<WorkBackgroundJobSnapshot>> ListBackgroundJobsAsync(
        string sessionId,
        string? stepId = null,
        string? planVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, session_id, run_id, step_id, plan_version, status,
                   created_at, started_at, completed_at, error_message
            FROM work_background_jobs
            WHERE session_id = $sessionId
              AND ($stepId IS NULL OR step_id = $stepId)
              AND ($planVersion IS NULL OR plan_version = $planVersion)
            ORDER BY created_at, job_id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$stepId", (object?)stepId ?? DBNull.Value);
        command.Parameters.AddWithValue("$planVersion", (object?)planVersion ?? DBNull.Value);

        var list = new List<WorkBackgroundJobSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadBackgroundJob(reader));
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
                or WorkStepLifecycleStatus.WaitingForCredentials
                or WorkStepLifecycleStatus.Pending);

        var sentCallIds = await ListSentToolCallIdsAsync(sessionId, ct).ConfigureAwait(false);
        var backgroundJobs = await ListBackgroundJobsAsync(sessionId, planVersion: planVersion, ct: ct)
            .ConfigureAwait(false);
        var revisions = await ListPlanRevisionsAsync(sessionId, ct).ConfigureAwait(false);
        return new WorkResumeState(
            status,
            gm,
            planVersion,
            planMessageId,
            [.. steps],
            current,
            BackgroundJobs: [.. backgroundJobs],
            SentToolCallIds: sentCallIds,
            RequiresManualIntervention: requiresManual,
            PendingApprovalRequestId: pendingApproval,
            PlanRevisions: [.. revisions]);
    }

    public async Task<IReadOnlyList<WorkPlanRevisionSnapshot>> ListPlanRevisionsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, run_id, plan_version, plan_message_id,
                   previous_plan_version, created_at
            FROM work_plan_revisions
            WHERE session_id = $sessionId
            ORDER BY created_at, plan_version;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var revisions = new List<WorkPlanRevisionSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            revisions.Add(new WorkPlanRevisionSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseRequiredTimestamp(reader.GetString(5))));
        }

        return revisions;
    }

    private async Task UpdateLatestStepAttemptStatusAsync(
        string stepId,
        string planVersion,
        string status,
        string? completedAt,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE work_step_attempts
            SET status = $status,
                completed_at = $completedAt
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND attempt_number = (
                  SELECT MAX(attempt_number) FROM work_step_attempts
                  WHERE step_id = $stepId AND plan_version = $planVersion);
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static WorkBackgroundJobSnapshot ReadBackgroundJob(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseBackgroundJobStatus(reader.GetString(5)),
            ParseRequiredTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.IsDBNull(7) ? null : reader.GetString(7)),
            ParseTimestamp(reader.IsDBNull(8) ? null : reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static void EnsureSameBackgroundJob(
        WorkBackgroundJobSnapshot job,
        string sessionId,
        string runId,
        string stepId,
        string planVersion)
    {
        if (!StringComparer.Ordinal.Equals(job.SessionId, sessionId)
            || !StringComparer.Ordinal.Equals(job.RunId, runId)
            || !StringComparer.Ordinal.Equals(job.StepId, stepId)
            || !StringComparer.Ordinal.Equals(job.PlanVersion, planVersion))
        {
            throw new InvalidOperationException(
                $"Background job '{job.JobId}' already exists with different linkage.");
        }
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
            ParseTimestamp(reader.IsDBNull(15) ? null : reader.GetString(15)),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20));
    }

    private async Task InsertPlanStepsAsync(
        string sessionId,
        string runId,
        WorkPlanDraft plan,
        IReadOnlyDictionary<string, string>? stepMessageIds,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var createdAt = FormatTimestamp(DateTimeOffset.UtcNow);
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
                    step_index, attempt_count, title, goal, is_background, depends_json,
                    step_message_id, reuses_step_id, replaces_step_id)
                VALUES(
                    $stepId, $planVersion, $runId, $sessionId, $agentId,
                    'pending', $hash, $createdAt, $parent, $depth,
                    $index, 0, $title, $goal, $background, $depends,
                    $stepMessageId, $reusesStepId, $replacesStepId);
                """;
            stepCommand.Parameters.AddWithValue("$stepId", step.StepId);
            stepCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
            stepCommand.Parameters.AddWithValue("$agentId", step.TargetAgentId);
            stepCommand.Parameters.AddWithValue("$hash", hash);
            stepCommand.Parameters.AddWithValue("$createdAt", createdAt);
            stepCommand.Parameters.AddWithValue("$parent", (object?)step.ParentStepId ?? DBNull.Value);
            stepCommand.Parameters.AddWithValue("$depth", step.Depth);
            stepCommand.Parameters.AddWithValue("$index", index);
            stepCommand.Parameters.AddWithValue("$title", (object?)step.Title ?? DBNull.Value);
            stepCommand.Parameters.AddWithValue("$goal", step.Goal);
            stepCommand.Parameters.AddWithValue("$background", step.IsBackground ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$depends", dependsJson);
            stepCommand.Parameters.AddWithValue(
                "$stepMessageId",
                stepMessageIds is not null && stepMessageIds.TryGetValue(step.StepId, out var messageId)
                    ? messageId
                    : DBNull.Value);
            stepCommand.Parameters.AddWithValue(
                "$reusesStepId",
                (object?)step.ReusesStepId ?? DBNull.Value);
            stepCommand.Parameters.AddWithValue(
                "$replacesStepId",
                (object?)step.ReplacesStepId ?? DBNull.Value);
            await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var dep in step.DependsOn ?? [])
            {
                await using var depCommand = _connection.CreateCommand();
                depCommand.Transaction = transaction;
                depCommand.CommandText = """
                    INSERT INTO work_step_dependencies(
                        step_id, plan_version, depends_on_step_id)
                    VALUES($stepId, $planVersion, $dep);
                    """;
                depCommand.Parameters.AddWithValue("$stepId", step.StepId);
                depCommand.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
                depCommand.Parameters.AddWithValue("$dep", dep);
                await depCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> GetCurrentPlanVersionAsync(
        string sessionId,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_version
            FROM work_sessions
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private async Task ValidateRevisionSequenceAsync(
        string sessionId,
        string? currentPlanVersion,
        WorkPlanDraft plan,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var existingRevision = _connection.CreateCommand();
        existingRevision.Transaction = transaction;
        existingRevision.CommandText = """
            SELECT 1
            FROM work_plan_revisions
            WHERE session_id = $sessionId AND plan_version = $planVersion;
            """;
        existingRevision.Parameters.AddWithValue("$sessionId", sessionId);
        existingRevision.Parameters.AddWithValue("$planVersion", plan.PlanVersion);
        if (await existingRevision.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                $"Work plan revision '{plan.PlanVersion}' is already published for session '{sessionId}'.");
        }

        if (currentPlanVersion is null
            || (string.Equals(currentPlanVersion, plan.PlanVersion, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(plan.PreviousPlanVersion)))
        {
            if (!string.Equals(plan.PlanVersion, "1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The first work plan revision must be version '1'.");
            }

            return;
        }

        if (!int.TryParse(currentPlanVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            || !int.TryParse(plan.PlanVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var next)
            || next != current + 1
            || !string.Equals(plan.PreviousPlanVersion, currentPlanVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Work plan revision '{plan.PlanVersion}' must increment '{currentPlanVersion}' by one and reference it as PreviousPlanVersion.");
        }
    }

    private async Task ValidateStepRevisionLinksAsync(
        string sessionId,
        WorkPlanDraft plan,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        foreach (var step in plan.Steps)
        {
            if (step.ReusesStepId is not null)
            {
                await EnsureHistoricalStepAsync(
                    sessionId,
                    step.ReusesStepId,
                    requireCompleted: true,
                    transaction,
                    ct).ConfigureAwait(false);
            }

            if (step.ReplacesStepId is not null)
            {
                await EnsureHistoricalStepAsync(
                    sessionId,
                    step.ReplacesStepId,
                    requireCompleted: false,
                    transaction,
                    ct).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureHistoricalStepAsync(
        string sessionId,
        string historicalStepId,
        bool requireCompleted,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historicalStepId);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status
            FROM work_steps
            WHERE session_id = $sessionId AND step_id = $stepId
            ORDER BY plan_version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$stepId", historicalStepId);
        var status = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (status is null || (requireCompleted && !string.Equals(status, "completed", StringComparison.Ordinal)))
        {
            var requirement = requireCompleted ? "a completed" : "an existing";
            throw new InvalidOperationException(
                $"Revision link '{historicalStepId}' must reference {requirement} historical step in session '{sessionId}'.");
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset ParseRequiredTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDbStatus(WorkSessionStatus status) => status switch
    {
        WorkSessionStatus.Planning => "planning",
        WorkSessionStatus.Executing => "executing",
        WorkSessionStatus.Paused => "paused",
        WorkSessionStatus.WaitingForApproval => "waiting_for_approval",
        WorkSessionStatus.WaitingForCredentials => "waiting_for_credentials",
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
        "waiting_for_credentials" => WorkSessionStatus.WaitingForCredentials,
        "completed" => WorkSessionStatus.Completed,
        "failed" => WorkSessionStatus.Failed,
        "cancelled" => WorkSessionStatus.Cancelled,
        "interrupted" => WorkSessionStatus.Interrupted,
        _ => Enum.Parse<WorkSessionStatus>(value, ignoreCase: true)
    };

    private static string ToDbStatus(WorkBackgroundJobStatus status) => status switch
    {
        WorkBackgroundJobStatus.Pending => "pending",
        WorkBackgroundJobStatus.Running => "running",
        WorkBackgroundJobStatus.Completed => "completed",
        WorkBackgroundJobStatus.Failed => "failed",
        WorkBackgroundJobStatus.Cancelled => "cancelled",
        WorkBackgroundJobStatus.Interrupted => "interrupted",
        _ => status.ToString().ToLowerInvariant()
    };

    private static WorkBackgroundJobStatus ParseBackgroundJobStatus(string value) => value.ToLowerInvariant() switch
    {
        "pending" => WorkBackgroundJobStatus.Pending,
        "running" => WorkBackgroundJobStatus.Running,
        "completed" => WorkBackgroundJobStatus.Completed,
        "failed" => WorkBackgroundJobStatus.Failed,
        "cancelled" => WorkBackgroundJobStatus.Cancelled,
        "interrupted" => WorkBackgroundJobStatus.Interrupted,
        _ => Enum.Parse<WorkBackgroundJobStatus>(value, ignoreCase: true)
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
        "waiting_for_credentials" => WorkStepLifecycleStatus.WaitingForCredentials,
        _ => Enum.Parse<WorkStepLifecycleStatus>(value, ignoreCase: true)
    };

    private static void ValidateOptionalStepIdentity(string? stepId, string? planVersion)
    {
        if ((stepId is null) != (planVersion is null))
        {
            throw new ArgumentException(
                "stepId and planVersion must either both be supplied or both be null.");
        }

        if (stepId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        }
    }

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
