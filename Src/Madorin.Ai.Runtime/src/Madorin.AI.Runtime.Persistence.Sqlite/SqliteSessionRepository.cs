using System.Globalization;
using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>SQLite persistence for Session and Run lifecycle metadata.</summary>
public sealed class SqliteSessionRepository(SqliteConnection conn) : ISessionRepository
{
    private const string CompletedWorkStepStatus = "completed";
    private const string FailedWorkStepStatus = "failed";
    private const string InterruptedWorkStepStatus = "interrupted";
    private const string PendingWorkStepStatus = "pending";
    private const string RunningWorkStepStatus = "running";
    private const string SucceededToolIntentStatus = "succeeded";

    private readonly SqliteConnection _connection = conn
        ?? throw new ArgumentNullException(nameof(conn));

    public SqliteConnection Connection => _connection;

    public Task<string> CreateSessionAsync(
        RuntimeMode mode,
        string idempotencyKey,
        TimeSpan keyRetention,
        CancellationToken ct = default) =>
        CreateSessionAsync(mode, idempotencyKey, keyRetention, requestHash: null, ct);

    public async Task<string> CreateSessionAsync(
        RuntimeMode mode,
        string idempotencyKey,
        TimeSpan keyRetention,
        string? requestHash,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ValidateRetention(keyRetention);
        var now = DateTimeOffset.UtcNow;
        var nowText = FormatTimestamp(now);

        using var transaction = _connection.BeginTransaction(deferred: false);
        var existing = await GetSessionIdempotencyAsync(
            idempotencyKey,
            now,
            transaction,
            ct).ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateRequestHash(existing.Value.RequestHash, requestHash, "Session");
            transaction.Commit();
            return existing.Value.SessionId;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        await using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES($sessionId, $mode, $status, $createdAt, $updatedAt);
                INSERT INTO session_idempotency(key, session_id, expires_at, request_hash)
                VALUES($key, $sessionId, $expiresAt, $requestHash);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$mode", mode.ToString());
            command.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            command.Parameters.AddWithValue("$createdAt", nowText);
            command.Parameters.AddWithValue("$updatedAt", nowText);
            command.Parameters.AddWithValue("$key", idempotencyKey);
            command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(now + keyRetention));
            command.Parameters.AddWithValue("$requestHash", (object?)requestHash ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return sessionId;
    }

    public Task<string> CreateRunAsync(
        string sessionId,
        string runIdempotencyKey,
        TimeSpan keyRetention,
        CancellationToken ct = default) =>
        CreateRunAsync(sessionId, runIdempotencyKey, keyRetention, requestHash: null, ct);

    public async Task<string> CreateRunAsync(
        string sessionId,
        string runIdempotencyKey,
        TimeSpan keyRetention,
        string? requestHash,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runIdempotencyKey);
        ValidateRetention(keyRetention);
        var now = DateTimeOffset.UtcNow;
        var nowText = FormatTimestamp(now);

        using var transaction = _connection.BeginTransaction(deferred: false);
        var existing = await GetRunIdempotencyAsync(
            runIdempotencyKey,
            now,
            transaction,
            ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Run idempotency key is already bound to another Session.");
            }

            ValidateRequestHash(existing.Value.RequestHash, requestHash, "Run");

            transaction.Commit();
            return existing.Value.RunId;
        }

        if (!await SessionExistsAsync(sessionId, transaction, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        var runSequence = await GetNextRunSequenceAsync(sessionId, transaction, ct)
            .ConfigureAwait(false);
        var runId = Guid.NewGuid().ToString("N");
        await using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runs(
                    run_id, session_id, status, run_sequence, created_at, updated_at)
                VALUES(
                    $runId, $sessionId, $status, $runSequence, $createdAt, $updatedAt);
                INSERT INTO run_idempotency(
                    key, run_id, session_id, expires_at, request_hash)
                VALUES($key, $runId, $sessionId, $expiresAt, $requestHash);
                UPDATE sessions
                SET updated_at = $updatedAt
                WHERE session_id = $sessionId;
                """;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$status", RunStatus.Accepted.ToString());
            command.Parameters.AddWithValue("$runSequence", runSequence);
            command.Parameters.AddWithValue("$createdAt", nowText);
            command.Parameters.AddWithValue("$updatedAt", nowText);
            command.Parameters.AddWithValue("$key", runIdempotencyKey);
            command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(now + keyRetention));
            command.Parameters.AddWithValue("$requestHash", (object?)requestHash ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return runId;
    }

    public async Task<RunStatus> GetRunStatusAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT status FROM runs WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is not string statusText)
        {
            throw new KeyNotFoundException($"Run '{runId}' was not found.");
        }

        return Enum.Parse<RunStatus>(statusText, ignoreCase: false);
    }

    public async Task TransitionRunStatusAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus targetStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!RunStateMachine.CanTransition(expectedStatus, targetStatus))
        {
            throw new InvalidOperationException(
                $"Run status cannot transition from {expectedStatus} to {targetStatus}.");
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET status = $to,
                updated_at = $updatedAt
            WHERE run_id = $runId AND status = $from;
            """;
        command.Parameters.AddWithValue("$to", targetStatus.ToString());
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$from", expectedStatus.ToString());
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Run '{runId}' is missing or is no longer in the {expectedStatus} state.");
        }
    }

    public async Task TransitionRunToTerminalAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus terminalStatus,
        string? terminalText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!RunStateMachine.IsTerminal(terminalStatus)
            || !RunStateMachine.CanTransition(expectedStatus, terminalStatus))
        {
            throw new InvalidOperationException(
                $"Run status cannot transition from {expectedStatus} to terminal state {terminalStatus}.");
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET status = $to,
                terminal_text = $terminalText,
                updated_at = $updatedAt
            WHERE run_id = $runId AND status = $from;
            """;
        command.Parameters.AddWithValue("$to", terminalStatus.ToString());
        command.Parameters.AddWithValue("$terminalText", (object?)terminalText ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$from", expectedStatus.ToString());
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Run '{runId}' is missing or is no longer in the {expectedStatus} state.");
        }
    }

    public async Task<RunSnapshot?> GetRunSnapshotAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, session_id, status, terminal_text
            FROM runs
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new RunSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<RunStatus>(reader.GetString(2), ignoreCase: false),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task MarkInterruptedAsync(CancellationToken ct = default)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);
        await using var runCommand = _connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = """
            UPDATE runs
            SET status = $interrupted,
                updated_at = $updatedAt
            WHERE status NOT IN (
                $completed, $failed, $cancelled, $interrupted, $waitingForApproval);
            """;
        runCommand.Parameters.AddWithValue("$interrupted", RunStatus.Interrupted.ToString());
        runCommand.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
        runCommand.Parameters.AddWithValue("$completed", RunStatus.Completed.ToString());
        runCommand.Parameters.AddWithValue("$failed", RunStatus.Failed.ToString());
        runCommand.Parameters.AddWithValue("$cancelled", RunStatus.Cancelled.ToString());
        runCommand.Parameters.AddWithValue(
            "$waitingForApproval",
            RunStatus.WaitingForApproval.ToString());
        await runCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = $interrupted
            WHERE status = $running;
            """;
        stepCommand.Parameters.AddWithValue("$interrupted", InterruptedWorkStepStatus);
        stepCommand.Parameters.AddWithValue("$running", RunningWorkStepStatus);
        await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    /// <summary>Creates or reuses the Running state for a work step (step-level idempotency only).</summary>
    public async Task<bool> TryStartWorkStepAsync(
        string stepId,
        string planVersion,
        string runId,
        string sessionId,
        string targetAgentId,
        string stepInputHash,
        string invocationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepInputHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);

        var createdAt = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        // Promote an existing Pending step created by plan materialization.
        await using var promoteCommand = _connection.CreateCommand();
        promoteCommand.Transaction = transaction;
        promoteCommand.CommandText = """
            UPDATE work_steps
            SET status = $running,
                run_id = $runId,
                session_id = $sessionId,
                target_agent_id = $targetAgentId,
                step_input_hash = $stepInputHash,
                invocation_id = $invocationId,
                started_at = $createdAt,
                attempt_count = COALESCE(attempt_count, 0) + 1
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND status = $pending;
            """;
        promoteCommand.Parameters.AddWithValue("$running", RunningWorkStepStatus);
        promoteCommand.Parameters.AddWithValue("$runId", runId);
        promoteCommand.Parameters.AddWithValue("$sessionId", sessionId);
        promoteCommand.Parameters.AddWithValue("$targetAgentId", targetAgentId);
        promoteCommand.Parameters.AddWithValue("$stepInputHash", stepInputHash);
        promoteCommand.Parameters.AddWithValue("$invocationId", invocationId);
        promoteCommand.Parameters.AddWithValue("$createdAt", createdAt);
        promoteCommand.Parameters.AddWithValue("$stepId", stepId);
        promoteCommand.Parameters.AddWithValue("$planVersion", planVersion);
        promoteCommand.Parameters.AddWithValue("$pending", PendingWorkStepStatus);
        if (await promoteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
        {
            await InsertStepAttemptAsync(
                stepId, planVersion, invocationId, RunningWorkStepStatus, createdAt, transaction, ct)
                .ConfigureAwait(false);
            transaction.Commit();
            return true;
        }

        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            INSERT OR IGNORE INTO work_steps(
                step_id,
                plan_version,
                run_id,
                session_id,
                target_agent_id,
                status,
                step_input_hash,
                created_at,
                invocation_id,
                started_at,
                attempt_count)
            VALUES(
                $stepId,
                $planVersion,
                $runId,
                $sessionId,
                $targetAgentId,
                $status,
                $stepInputHash,
                $createdAt,
                $invocationId,
                $createdAt,
                1);
            """;
        stepCommand.Parameters.AddWithValue("$stepId", stepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        stepCommand.Parameters.AddWithValue("$runId", runId);
        stepCommand.Parameters.AddWithValue("$sessionId", sessionId);
        stepCommand.Parameters.AddWithValue("$targetAgentId", targetAgentId);
        stepCommand.Parameters.AddWithValue("$status", RunningWorkStepStatus);
        stepCommand.Parameters.AddWithValue("$stepInputHash", stepInputHash);
        stepCommand.Parameters.AddWithValue("$createdAt", createdAt);
        stepCommand.Parameters.AddWithValue("$invocationId", invocationId);
        var inserted = await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;

        if (!inserted)
        {
            await ValidateExistingWorkStepAsync(
                stepId,
                planVersion,
                targetAgentId,
                stepInputHash,
                transaction,
                ct).ConfigureAwait(false);
            transaction.Commit();
            return false;
        }

        await InsertStepAttemptAsync(
            stepId, planVersion, invocationId, RunningWorkStepStatus, createdAt, transaction, ct)
            .ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    private async Task InsertStepAttemptAsync(
        string stepId,
        string planVersion,
        string invocationId,
        string status,
        string startedAt,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var attemptCommand = _connection.CreateCommand();
        attemptCommand.Transaction = transaction;
        attemptCommand.CommandText = """
            INSERT INTO work_step_attempts(
                step_id, plan_version, attempt_number, invocation_id, status, started_at)
            SELECT $stepId, $planVersion,
                   COALESCE((SELECT MAX(attempt_number) FROM work_step_attempts
                             WHERE step_id = $stepId AND plan_version = $planVersion), 0) + 1,
                   $invocationId, $status, $startedAt;
            """;
        attemptCommand.Parameters.AddWithValue("$stepId", stepId);
        attemptCommand.Parameters.AddWithValue("$planVersion", planVersion);
        attemptCommand.Parameters.AddWithValue("$invocationId", invocationId);
        attemptCommand.Parameters.AddWithValue("$status", status);
        attemptCommand.Parameters.AddWithValue("$startedAt", startedAt);
        await attemptCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns a completed work-step result from step-level state only.</summary>
    public async Task<string?> GetCompletedWorkStepResultAsync(
        string stepId,
        string planVersion,
        string stepInputHash,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepInputHash);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT result_json
            FROM work_steps
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND step_input_hash = $stepInputHash
              AND status = $completed;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$stepInputHash", stepInputHash);
        command.Parameters.AddWithValue("$completed", CompletedWorkStepStatus);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>Marks a work step Completed after outputs are persisted.</summary>
    public async Task CompleteWorkStepAsync(
        string stepId,
        string planVersion,
        string stepInputHash,
        string resultJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepInputHash);
        ArgumentNullException.ThrowIfNull(resultJson);

        var completedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = $completed,
                result_json = $resultJson,
                completed_at = $completedAt
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND step_input_hash = $stepInputHash
              AND status = $running;
            """;
        stepCommand.Parameters.AddWithValue("$completed", CompletedWorkStepStatus);
        stepCommand.Parameters.AddWithValue("$resultJson", resultJson);
        stepCommand.Parameters.AddWithValue("$completedAt", completedAt);
        stepCommand.Parameters.AddWithValue("$stepId", stepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        stepCommand.Parameters.AddWithValue("$stepInputHash", stepInputHash);
        stepCommand.Parameters.AddWithValue("$running", RunningWorkStepStatus);
        if (await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Work step '{stepId}' is missing or is no longer running.");
        }

        await using var attemptCommand = _connection.CreateCommand();
        attemptCommand.Transaction = transaction;
        attemptCommand.CommandText = """
            UPDATE work_step_attempts
            SET status = $completed,
                completed_at = $completedAt
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND attempt_number = (
                  SELECT MAX(attempt_number) FROM work_step_attempts
                  WHERE step_id = $stepId AND plan_version = $planVersion);
            """;
        attemptCommand.Parameters.AddWithValue("$completed", CompletedWorkStepStatus);
        attemptCommand.Parameters.AddWithValue("$completedAt", completedAt);
        attemptCommand.Parameters.AddWithValue("$stepId", stepId);
        attemptCommand.Parameters.AddWithValue("$planVersion", planVersion);
        await attemptCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        transaction.Commit();
    }

    /// <summary>Marks a work step as failed.</summary>
    public async Task FailWorkStepAsync(
        string stepId,
        string planVersion,
        string errorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        var completedAt = FormatTimestamp(DateTimeOffset.UtcNow);

        using var transaction = _connection.BeginTransaction(deferred: false);
        await using var stepCommand = _connection.CreateCommand();
        stepCommand.Transaction = transaction;
        stepCommand.CommandText = """
            UPDATE work_steps
            SET status = $failed,
                error_message = $errorMessage,
                completed_at = $completedAt
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND status = $running;
            """;
        stepCommand.Parameters.AddWithValue("$failed", FailedWorkStepStatus);
        stepCommand.Parameters.AddWithValue("$errorMessage", errorMessage);
        stepCommand.Parameters.AddWithValue("$completedAt", completedAt);
        stepCommand.Parameters.AddWithValue("$stepId", stepId);
        stepCommand.Parameters.AddWithValue("$planVersion", planVersion);
        stepCommand.Parameters.AddWithValue("$running", RunningWorkStepStatus);
        await stepCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var attemptCommand = _connection.CreateCommand();
        attemptCommand.Transaction = transaction;
        attemptCommand.CommandText = """
            UPDATE work_step_attempts
            SET status = $failed,
                error_message = $errorMessage,
                completed_at = $completedAt
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND attempt_number = (
                  SELECT MAX(attempt_number) FROM work_step_attempts
                  WHERE step_id = $stepId AND plan_version = $planVersion);
            """;
        attemptCommand.Parameters.AddWithValue("$failed", FailedWorkStepStatus);
        attemptCommand.Parameters.AddWithValue("$errorMessage", errorMessage);
        attemptCommand.Parameters.AddWithValue("$completedAt", completedAt);
        attemptCommand.Parameters.AddWithValue("$stepId", stepId);
        attemptCommand.Parameters.AddWithValue("$planVersion", planVersion);
        await attemptCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    /// <summary>Loads the Session mode and its latest Run state for resume.</summary>
    public async Task<(RuntimeMode Mode, string LastRunId, RunStatus LastStatus)?>
        GetSessionResumeStateAsync(
            string sessionId,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT sessions.mode, runs.run_id, runs.status
            FROM sessions
            INNER JOIN runs ON runs.session_id = sessions.session_id
            WHERE sessions.session_id = $sessionId
            ORDER BY runs.run_sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return (
            Enum.Parse<RuntimeMode>(reader.GetString(0), ignoreCase: false),
            reader.GetString(1),
            Enum.Parse<RunStatus>(reader.GetString(2), ignoreCase: false));
    }

    public async Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(
        string? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var position = await ResolveCursorAsync(cursor, ct).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = position is null
            ? """
                SELECT session_id, mode, status, updated_at
                FROM sessions
                ORDER BY updated_at DESC, session_id DESC
                LIMIT $pageSize;
                """
            : """
                SELECT session_id, mode, status, updated_at
                FROM sessions
                WHERE updated_at < $updatedAt
                   OR (updated_at = $updatedAt AND session_id < $sessionId)
                ORDER BY updated_at DESC, session_id DESC
                LIMIT $pageSize;
                """;
        command.Parameters.AddWithValue("$pageSize", pageSize);
        if (position is not null)
        {
            command.Parameters.AddWithValue("$updatedAt", position.Value.UpdatedAt);
            command.Parameters.AddWithValue("$sessionId", position.Value.SessionId);
        }

        var sessions = new List<SessionDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(new SessionDescriptor(
                reader.GetString(0),
                Enum.Parse<RuntimeMode>(reader.GetString(1), ignoreCase: false),
                Enum.Parse<SessionStatus>(reader.GetString(2), ignoreCase: false),
                DateTimeOffset.ParseExact(
                    reader.GetString(3),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Title: null));
        }

        return sessions;
    }

    public async Task<PersistedSessionSnapshot?> GetSessionSnapshotAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        RuntimeMode mode;
        SessionStatus status;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        await using (var sessionCommand = _connection.CreateCommand())
        {
            sessionCommand.CommandText = """
                SELECT mode, status, created_at, updated_at
                FROM sessions
                WHERE session_id = $sessionId;
                """;
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await sessionCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            mode = Enum.Parse<RuntimeMode>(reader.GetString(0), ignoreCase: false);
            status = Enum.Parse<SessionStatus>(reader.GetString(1), ignoreCase: false);
            createdAt = ParseTimestamp(reader.GetString(2));
            updatedAt = ParseTimestamp(reader.GetString(3));
        }

        int? selectionVersion = null;
        string? selectionJson = null;
        await using (var selectionCommand = _connection.CreateCommand())
        {
            selectionCommand.CommandText = """
                SELECT selection_version, selection_json
                FROM session_selections
                WHERE session_id = $sessionId;
                """;
            selectionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await selectionCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                selectionVersion = reader.GetInt32(0);
                selectionJson = reader.GetString(1);
            }
        }

        RunSnapshot? latestRun = null;
        await using (var runCommand = _connection.CreateCommand())
        {
            runCommand.CommandText = """
                SELECT run_id, status, terminal_text
                FROM runs
                WHERE session_id = $sessionId
                ORDER BY run_sequence DESC
                LIMIT 1;
                """;
            runCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await runCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                latestRun = new RunSnapshot(
                    reader.GetString(0),
                    sessionId,
                    Enum.Parse<RunStatus>(reader.GetString(1), ignoreCase: false),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var agentSnapshots = new List<AgentSnapshot>();
        await using (var agentCommand = _connection.CreateCommand())
        {
            agentCommand.CommandText = """
                SELECT agent_id, prompt_template_version, prompt_hash, provider_id, model_id
                FROM agent_snapshots
                WHERE session_id = $sessionId
                ORDER BY agent_id;
                """;
            agentCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await agentCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                agentSnapshots.Add(new AgentSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        long lastGsn;
        await using (var gsnCommand = _connection.CreateCommand())
        {
            gsnCommand.CommandText = "SELECT COALESCE(MAX(gsn), 0) FROM event_outbox;";
            lastGsn = Convert.ToInt64(
                await gsnCommand.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        return new PersistedSessionSnapshot(
            sessionId,
            mode,
            status,
            createdAt,
            updatedAt,
            selectionVersion,
            selectionJson,
            latestRun,
            [.. agentSnapshots],
            lastGsn);
    }

    public async Task<bool> TrySaveSessionSelectionAsync(
        string sessionId,
        int? expectedSelectionVersion,
        int selectionVersion,
        string selectionJson,
        IReadOnlyList<AgentSnapshot> agentSnapshots,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionVersion);
        ArgumentNullException.ThrowIfNull(selectionJson);
        ArgumentNullException.ThrowIfNull(agentSnapshots);
        if (expectedSelectionVersion is { } expected && selectionVersion <= expected)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectionVersion),
                "The new Selection version must be greater than the expected version.");
        }

        if (agentSnapshots.Select(static snapshot => snapshot.AgentId).Distinct(StringComparer.Ordinal)
            .Count() != agentSnapshots.Count)
        {
            throw new ArgumentException("Agent snapshots must have unique Agent IDs.", nameof(agentSnapshots));
        }

        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        int changed;
        await using (var selectionCommand = _connection.CreateCommand())
        {
            selectionCommand.Transaction = transaction;
            selectionCommand.CommandText = expectedSelectionVersion is null
                ? """
                    INSERT OR IGNORE INTO session_selections(
                        session_id, selection_version, selection_json, updated_at)
                    VALUES($sessionId, $selectionVersion, $selectionJson, $updatedAt);
                    """
                : """
                    UPDATE session_selections
                    SET selection_version = $selectionVersion,
                        selection_json = $selectionJson,
                        updated_at = $updatedAt
                    WHERE session_id = $sessionId
                      AND selection_version = $expectedSelectionVersion;
                    """;
            selectionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            selectionCommand.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            selectionCommand.Parameters.AddWithValue("$selectionJson", selectionJson);
            selectionCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            if (expectedSelectionVersion is { } expectedVersion)
            {
                selectionCommand.Parameters.AddWithValue(
                    "$expectedSelectionVersion",
                    expectedVersion);
            }

            changed = await selectionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (changed == 0)
        {
            await using var currentCommand = _connection.CreateCommand();
            currentCommand.Transaction = transaction;
            currentCommand.CommandText = """
                SELECT selection_version, selection_json
                FROM session_selections
                WHERE session_id = $sessionId;
                """;
            currentCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await currentCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var isIdempotentRetry = await reader.ReadAsync(ct).ConfigureAwait(false)
                && reader.GetInt32(0) == selectionVersion
                && string.Equals(reader.GetString(1), selectionJson, StringComparison.Ordinal);
            transaction.Commit();
            return isIdempotentRetry;
        }

        await using (var deleteCommand = _connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM agent_snapshots WHERE session_id = $sessionId;";
            deleteCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await deleteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var snapshot in agentSnapshots)
        {
            await using var agentCommand = _connection.CreateCommand();
            agentCommand.Transaction = transaction;
            agentCommand.CommandText = """
                INSERT INTO agent_snapshots(
                    session_id,
                    agent_id,
                    prompt_template_version,
                    prompt_hash,
                    provider_id,
                    model_id)
                VALUES(
                    $sessionId,
                    $agentId,
                    $promptTemplateVersion,
                    $promptHash,
                    $providerId,
                    $modelId);
                """;
            agentCommand.Parameters.AddWithValue("$sessionId", sessionId);
            agentCommand.Parameters.AddWithValue("$agentId", snapshot.AgentId);
            agentCommand.Parameters.AddWithValue(
                "$promptTemplateVersion",
                snapshot.PromptTemplateVersion);
            agentCommand.Parameters.AddWithValue("$promptHash", snapshot.PromptHash);
            agentCommand.Parameters.AddWithValue(
                "$providerId",
                (object?)snapshot.ProviderId ?? DBNull.Value);
            agentCommand.Parameters.AddWithValue("$modelId", (object?)snapshot.ModelId ?? DBNull.Value);
            await agentCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var sessionCommand = _connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = """
                UPDATE sessions
                SET updated_at = $updatedAt
                WHERE session_id = $sessionId;
                """;
            sessionCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            if (await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }
        }

        transaction.Commit();
        return true;
    }

    public async Task<bool> TrySaveSessionSelectionWithMeetingAsync(
        string sessionId,
        int expectedSelectionVersion,
        int selectionVersion,
        string selectionJson,
        IReadOnlyList<AgentSnapshot> agentSnapshots,
        IReadOnlyList<MeetingParticipantInput> meetingParticipants,
        string policyJson,
        string policyHash,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionVersion);
        ArgumentNullException.ThrowIfNull(selectionJson);
        ArgumentNullException.ThrowIfNull(agentSnapshots);
        ArgumentNullException.ThrowIfNull(meetingParticipants);
        ArgumentNullException.ThrowIfNull(policyJson);
        ArgumentNullException.ThrowIfNull(policyHash);
        if (selectionVersion <= expectedSelectionVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectionVersion),
                "The new Selection version must be greater than the expected version.");
        }

        if (agentSnapshots.Select(static snapshot => snapshot.AgentId).Distinct(StringComparer.Ordinal)
            .Count() != agentSnapshots.Count)
        {
            throw new ArgumentException("Agent snapshots must have unique Agent IDs.", nameof(agentSnapshots));
        }

        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        int changed;
        await using (var selectionCommand = _connection.CreateCommand())
        {
            selectionCommand.Transaction = transaction;
            selectionCommand.CommandText = """
                UPDATE session_selections
                SET selection_version = $selectionVersion,
                    selection_json = $selectionJson,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND selection_version = $expectedSelectionVersion;
                """;
            selectionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            selectionCommand.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            selectionCommand.Parameters.AddWithValue("$selectionJson", selectionJson);
            selectionCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            selectionCommand.Parameters.AddWithValue("$expectedSelectionVersion", expectedSelectionVersion);
            changed = await selectionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (changed == 0)
        {
            await using var currentCommand = _connection.CreateCommand();
            currentCommand.Transaction = transaction;
            currentCommand.CommandText = """
                SELECT selection_version, selection_json
                FROM session_selections
                WHERE session_id = $sessionId;
                """;
            currentCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await currentCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var isIdempotentRetry = await reader.ReadAsync(ct).ConfigureAwait(false)
                && reader.GetInt32(0) == selectionVersion
                && string.Equals(reader.GetString(1), selectionJson, StringComparison.Ordinal);
            transaction.Commit();
            return isIdempotentRetry;
        }

        await using (var deleteCommand = _connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM agent_snapshots WHERE session_id = $sessionId;";
            deleteCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await deleteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var snapshot in agentSnapshots)
        {
            await using var agentCommand = _connection.CreateCommand();
            agentCommand.Transaction = transaction;
            agentCommand.CommandText = """
                INSERT INTO agent_snapshots(
                    session_id,
                    agent_id,
                    prompt_template_version,
                    prompt_hash,
                    provider_id,
                    model_id)
                VALUES(
                    $sessionId,
                    $agentId,
                    $promptTemplateVersion,
                    $promptHash,
                    $providerId,
                    $modelId);
                """;
            agentCommand.Parameters.AddWithValue("$sessionId", sessionId);
            agentCommand.Parameters.AddWithValue("$agentId", snapshot.AgentId);
            agentCommand.Parameters.AddWithValue("$promptTemplateVersion", snapshot.PromptTemplateVersion);
            agentCommand.Parameters.AddWithValue("$promptHash", snapshot.PromptHash);
            agentCommand.Parameters.AddWithValue("$providerId", (object?)snapshot.ProviderId ?? DBNull.Value);
            agentCommand.Parameters.AddWithValue("$modelId", (object?)snapshot.ModelId ?? DBNull.Value);
            await agentCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var meetingSessionCommand = _connection.CreateCommand())
        {
            meetingSessionCommand.Transaction = transaction;
            meetingSessionCommand.CommandText = """
                UPDATE meeting_sessions
                SET selection_version = $selectionVersion,
                    policy_json = $policyJson,
                    policy_hash = $policyHash,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND selection_version = $expectedSelectionVersion;
                """;
            meetingSessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            meetingSessionCommand.Parameters.AddWithValue("$policyJson", policyJson);
            meetingSessionCommand.Parameters.AddWithValue("$policyHash", policyHash);
            meetingSessionCommand.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            meetingSessionCommand.Parameters.AddWithValue("$expectedSelectionVersion", expectedSelectionVersion);
            meetingSessionCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            if (await meetingSessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                transaction.Rollback();
                return false;
            }
        }

        await using var softDeleteCommand = _connection.CreateCommand();
        softDeleteCommand.Transaction = transaction;
        var notInClause = string.Empty;
        if (meetingParticipants.Count > 0)
        {
            var placeholders = new string[meetingParticipants.Count];
            for (var i = 0; i < meetingParticipants.Count; i++)
            {
                placeholders[i] = "$participant" + i.ToString(CultureInfo.InvariantCulture);
            }

            notInClause = " AND participant_id NOT IN (" + string.Join(", ", placeholders) + ")";
        }

        softDeleteCommand.CommandText = """
            UPDATE meeting_participants
            SET status = 'removed',
                removed_selection_version = $selectionVersion,
                removed_at = $updatedAt,
                updated_at = $updatedAt
            WHERE session_id = $sessionId
              AND status IN ('active', 'standby')
            """ + notInClause + ";";
        softDeleteCommand.Parameters.AddWithValue("$sessionId", sessionId);
        softDeleteCommand.Parameters.AddWithValue("$selectionVersion", selectionVersion);
        softDeleteCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
        for (var i = 0; i < meetingParticipants.Count; i++)
        {
            softDeleteCommand.Parameters.AddWithValue(
                "$participant" + i.ToString(CultureInfo.InvariantCulture),
                meetingParticipants[i].ParticipantId);
        }

        await softDeleteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        foreach (var p in meetingParticipants)
        {
            await using var participantCommand = _connection.CreateCommand();
            participantCommand.Transaction = transaction;
            participantCommand.CommandText = """
                INSERT INTO meeting_participants(
                    session_id, participant_id, agent_id, agent_ref_json,
                    display_name, join_order, status, joined_selection_version,
                    joined_at, updated_at)
                VALUES(
                    $sessionId, $participantId, $agentId, $agentRefJson,
                    $displayName, $joinOrder, $status, $selectionVersion,
                    $joinedAt, $updatedAt)
                ON CONFLICT(session_id, participant_id) DO UPDATE SET
                    agent_id = $agentId,
                    agent_ref_json = $agentRefJson,
                    display_name = $displayName,
                    join_order = $joinOrder,
                    status = $status,
                    removed_selection_version = NULL,
                    removed_at = NULL,
                    updated_at = $updatedAt;
                """;
            participantCommand.Parameters.AddWithValue("$sessionId", sessionId);
            participantCommand.Parameters.AddWithValue("$participantId", p.ParticipantId);
            participantCommand.Parameters.AddWithValue("$agentId", p.AgentId);
            participantCommand.Parameters.AddWithValue("$agentRefJson", (object?)p.AgentRefJson ?? DBNull.Value);
            participantCommand.Parameters.AddWithValue("$displayName", (object?)p.DisplayName ?? DBNull.Value);
            participantCommand.Parameters.AddWithValue("$joinOrder", p.JoinOrder);
            participantCommand.Parameters.AddWithValue("$status", p.Status);
            participantCommand.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            participantCommand.Parameters.AddWithValue("$joinedAt", updatedAt);
            participantCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            await participantCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var sessionCommand = _connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = """
                UPDATE sessions
                SET updated_at = $updatedAt
                WHERE session_id = $sessionId;
                """;
            sessionCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            if (await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }
        }

        transaction.Commit();
        return true;
    }

    public async Task SaveInvocationSnapshotAsync(
        string runId,
        string sessionId,
        InvocationSnapshot snapshot,
        string snapshotJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.InvocationId);

        var startedAt = snapshot.StartedAt.ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        using var transaction = _connection.BeginTransaction(deferred: false);
        int inserted;
        await using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO invocation_snapshots(
                    invocation_id,
                    run_id,
                    session_id,
                    snapshot_json,
                    started_at)
                VALUES(
                    $invocationId,
                    $runId,
                    $sessionId,
                    $snapshotJson,
                    $startedAt);
                """;
            insert.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
            insert.Parameters.AddWithValue("$runId", runId);
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$snapshotJson", snapshotJson);
            insert.Parameters.AddWithValue("$startedAt", startedAt);
            inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            await using var existing = _connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT run_id, session_id, snapshot_json, started_at
                FROM invocation_snapshots
                WHERE invocation_id = $invocationId;
                """;
            existing.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
            await using var reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || !string.Equals(reader.GetString(0), runId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), sessionId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), snapshotJson, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(3), startedAt, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invocation snapshot '{snapshot.InvocationId}' already exists with different immutable fields.");
            }
        }

        transaction.Commit();
    }

    public async Task<string?> GetInvocationSnapshotJsonAsync(
        string invocationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_json
            FROM invocation_snapshots
            WHERE invocation_id = $invocationId;
            """;
        command.Parameters.AddWithValue("$invocationId", invocationId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string json ? json : null;
    }
    public async Task<IReadOnlyList<MessageIndexEntry>> ListMessageIndexAsync(
        string sessionId,
        long? cursor,
        int pageSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (cursor is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        await using (var sessionCommand = _connection.CreateCommand())
        {
            sessionCommand.CommandText = "SELECT 1 FROM sessions WHERE session_id = $sessionId;";
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            if (await sessionCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                message_id,
                session_id,
                sequence,
                invocation_id,
                agent_id,
                role,
                file_offset,
                record_length,
                created_at
            FROM message_index
            WHERE session_id = $sessionId
              AND sequence > $cursor
            ORDER BY sequence
            LIMIT $pageSize;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$cursor", cursor ?? 0);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        var entries = new List<MessageIndexEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new MessageIndexEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                ParseTimestamp(reader.GetString(8))));
        }

        return entries;
    }

    private async Task<(string SessionId, string? RequestHash)?> GetSessionIdempotencyAsync(
        string key,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT session_id, expires_at, request_hash
            FROM session_idempotency
            WHERE key = $key;
            """;
        select.Parameters.AddWithValue("$key", key);
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? sessionId = null;
        string? requestHash = null;
        DateTimeOffset? expiresAt = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessionId = reader.GetString(0);
            expiresAt = ParseTimestamp(reader.GetString(1));
            requestHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (expiresAt > now)
        {
            return (sessionId!, requestHash);
        }

        await DeleteIdempotencyAsync("session_idempotency", key, transaction, ct)
            .ConfigureAwait(false);
        return null;
    }

    private async Task ValidateExistingWorkStepAsync(
        string stepId,
        string planVersion,
        string targetAgentId,
        string stepInputHash,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_agent_id, step_input_hash
            FROM work_steps
            WHERE step_id = $stepId AND plan_version = $planVersion;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidDataException($"Work step '{stepId}' could not be reloaded.");
        }

        var existingAgentId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var existingInputHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.Equals(existingAgentId, targetAgentId, StringComparison.Ordinal)
            || !string.Equals(existingInputHash, stepInputHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Work step '{stepId}' is already bound to different inputs or an Agent.");
        }
    }

    private async Task<(string RunId, string SessionId, string? RequestHash)?> GetRunIdempotencyAsync(
        string key,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT run_id, session_id, expires_at, request_hash
            FROM run_idempotency
            WHERE key = $key;
            """;
        select.Parameters.AddWithValue("$key", key);
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? runId = null;
        string? sessionId = null;
        string? requestHash = null;
        DateTimeOffset? expiresAt = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runId = reader.GetString(0);
            sessionId = reader.GetString(1);
            expiresAt = ParseTimestamp(reader.GetString(2));
            requestHash = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (expiresAt > now)
        {
            return (runId!, sessionId!, requestHash);
        }

        await DeleteIdempotencyAsync("run_idempotency", key, transaction, ct)
            .ConfigureAwait(false);
        return null;
    }

    private static void ValidateRequestHash(
        string? existingHash,
        string? requestHash,
        string requestKind)
    {
        if (existingHash is not null
            && requestHash is not null
            && !string.Equals(existingHash, requestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {requestKind} idempotency key was reused with a different request payload.");
        }
    }

    private async Task DeleteIdempotencyAsync(
        string table,
        string key,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table switch
        {
            "session_idempotency" => "DELETE FROM session_idempotency WHERE key = $key;",
            "run_idempotency" => "DELETE FROM run_idempotency WHERE key = $key;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> SessionExistsAsync(
        string sessionId,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sessions WHERE session_id = $sessionId LIMIT 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private async Task<long> GetNextRunSequenceAsync(
        string sessionId,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(run_sequence), -1) + 1
            FROM runs
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<(string UpdatedAt, string SessionId)?> ResolveCursorAsync(
        string? cursor,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var separator = cursor.IndexOf('|', StringComparison.Ordinal);
        if (separator > 0 && separator < cursor.Length - 1)
        {
            var updatedAt = cursor[..separator];
            _ = ParseTimestamp(updatedAt);
            return (updatedAt, cursor[(separator + 1)..]);
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM sessions WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", cursor);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string updatedAtValue
            ? (updatedAtValue, cursor)
            : throw new ArgumentException("The Session cursor is invalid.", nameof(cursor));
    }

    private static void ValidateRetention(TimeSpan keyRetention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keyRetention, TimeSpan.Zero);
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
