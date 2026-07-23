using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>SQLite persistence for Meeting session lifecycle.</summary>
public sealed class SqliteMeetingRepository(
    SqliteConnection conn,
    SqliteMeetingRepositoryOptions? options = null)
{
    private readonly SqliteConnection _connection = conn
        ?? throw new ArgumentNullException(nameof(conn));
    private readonly SqliteMeetingRepositoryOptions _options = options ?? new();

    #region 1. CreateMeetingSessionAsync

    public Task CreateMeetingSessionAsync(
        string sessionId,
        string runId,
        string policyJson,
        string policyHash,
        IReadOnlyList<MeetingParticipantInput> initialParticipants,
        CancellationToken ct = default)
        => CreateMeetingSessionAsync(
            sessionId, runId, policyJson, policyHash,
            initialParticipants, selectionVersion: 0, ct);

    public async Task CreateMeetingSessionAsync(
        string sessionId,
        string runId,
        string policyJson,
        string policyHash,
        IReadOnlyList<MeetingParticipantInput> initialParticipants,
        int selectionVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(policyJson);
        ArgumentNullException.ThrowIfNull(policyHash);
        ArgumentNullException.ThrowIfNull(initialParticipants);
        if (selectionVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectionVersion), "Selection version must be >= 0.");
        }
        ValidateParticipants(initialParticipants);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        int inserted;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO meeting_sessions(
                    session_id, run_id, status, current_round,
                    policy_json, policy_hash, selection_version,
                    created_at, updated_at)
                VALUES(
                    $sessionId, $runId, $status, 0,
                    $policyJson, $policyHash, $selectionVersion,
                    $createdAt, $updatedAt);
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$status", "Running");
            cmd.Parameters.AddWithValue("$policyJson", policyJson);
            cmd.Parameters.AddWithValue("$policyHash", policyHash);
            cmd.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            cmd.Parameters.AddWithValue("$createdAt", now);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            transaction.Commit();
            return;
        }

        foreach (var p in initialParticipants)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO meeting_participants(
                    session_id, participant_id, agent_id, agent_ref_json,
                    display_name, join_order, status, joined_selection_version,
                    joined_at, updated_at)
                VALUES(
                    $sessionId, $participantId, $agentId, $agentRefJson,
                    $displayName, $joinOrder, $status, $selectionVersion,
                    $joinedAt, $updatedAt);
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$participantId", p.ParticipantId);
            cmd.Parameters.AddWithValue("$agentId", p.AgentId);
            cmd.Parameters.AddWithValue("$agentRefJson", (object?)p.AgentRefJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$displayName", (object?)p.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$joinOrder", p.JoinOrder);
            cmd.Parameters.AddWithValue("$status", p.Status);
            cmd.Parameters.AddWithValue("$selectionVersion", selectionVersion);
            cmd.Parameters.AddWithValue("$joinedAt", now);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    #endregion

    #region 2. CreateRoundWithFirstInvocationAsync

    public async Task<MeetingInvocationRecord> CreateRoundWithFirstInvocationAsync(
        string sessionId,
        string runId,
        int expectedCurrentRound,
        int newRoundIndex,
        MeetingInvocationScheduleInput firstInvocation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(firstInvocation);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET current_round = $newRound,
                    status = 'Running',
                    run_id = $runId,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND current_round = $expectedRound;
                """;
            cmd.Parameters.AddWithValue("$newRound", newRoundIndex);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$expectedRound", expectedCurrentRound);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{sessionId}' round conflict: expected {expectedCurrentRound}.");
            }
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO meeting_rounds(
                    session_id, round_index, run_id, status,
                    first_invocation_id, created_at, started_at)
                VALUES(
                    $sessionId, $roundIndex, $runId, 'Running',
                    $firstInvocationId, $createdAt, $startedAt);
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$roundIndex", newRoundIndex);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$firstInvocationId", firstInvocation.InvocationId);
            cmd.Parameters.AddWithValue("$createdAt", now);
            cmd.Parameters.AddWithValue("$startedAt", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var scheduled = await InsertScheduledInvocationAsync(
            sessionId, runId, newRoundIndex, 0, firstInvocation, now, transaction, ct)
            .ConfigureAwait(false);

        transaction.Commit();
        await InjectFailureAsync(SqliteMeetingRepositoryFailurePoint.AfterRoundCreationCommit)
            .ConfigureAwait(false);
        return scheduled;
    }

    #endregion

    #region 3. TransitionInvocationStatusAsync

    public async Task TransitionInvocationStatusAsync(
        string invocationId,
        string expectedStatus,
        string targetStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStatus);

        if (!string.Equals(expectedStatus, "Scheduled", StringComparison.Ordinal)
            || !string.Equals(targetStatus, "Running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Only the Scheduled -> Running transition is allowed; got '{expectedStatus}' -> '{targetStatus}'.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE meeting_invocations
            SET status = 'Running',
                started_at = $now
            WHERE invocation_id = $invocationId
              AND status = 'Scheduled';
            """;
        cmd.Parameters.AddWithValue("$invocationId", invocationId);
        cmd.Parameters.AddWithValue("$now", now);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is missing or not in 'Scheduled' state.");
        }
    }

    #endregion

    #region 4. CompleteInvocationAsync

    public async Task<MeetingInvocationRecord?> CompleteInvocationAsync(
        string invocationId,
        string targetStatus,
        string? messageId,
        string? selectorDecisionJson,
        string? errorCode,
        string? errorMessage,
        MeetingInvocationScheduleInput? nextInvocation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStatus);

        if (targetStatus is not ("Completed" or "Skipped" or "Failed" or "Interrupted"))
        {
            throw new InvalidOperationException(
                $"Target status '{targetStatus}' is not a valid completion status.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = targetStatus is "Interrupted"
                ? """
                    UPDATE meeting_invocations
                    SET status = $target,
                        message_id = $messageId,
                        selector_decision_json = $decisionJson,
                        error_code = $errorCode,
                        error_message = $errorMessage,
                        completed_at = $now
                    WHERE invocation_id = $invocationId
                      AND status IN ('Scheduled', 'Running');
                    """
                : """
                    UPDATE meeting_invocations
                    SET status = $target,
                        message_id = $messageId,
                        selector_decision_json = $decisionJson,
                        error_code = $errorCode,
                        error_message = $errorMessage,
                        completed_at = $now
                    WHERE invocation_id = $invocationId
                      AND status = 'Running';
                    """;
            cmd.Parameters.AddWithValue("$target", targetStatus);
            cmd.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$decisionJson", (object?)selectorDecisionJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$invocationId", invocationId);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' is missing or not in a completable state.");
            }
        }

        MeetingInvocationRecord? next = null;
        if (nextInvocation is not null)
        {
            var ctx = await LoadInvocationContextAsync(invocationId, transaction, ct)
                .ConfigureAwait(false);
            next = await InsertScheduledInvocationAsync(
                ctx.SessionId, ctx.RunId, ctx.RoundIndex, ctx.Ordinal + 1,
                nextInvocation, now, transaction, ct)
                .ConfigureAwait(false);
        }

        transaction.Commit();
        var failurePoint = selectorDecisionJson is null
            ? SqliteMeetingRepositoryFailurePoint.AfterInvocationCompletionCommit
            : SqliteMeetingRepositoryFailurePoint.AfterSelectorDecisionCommit;
        await InjectFailureAsync(failurePoint).ConfigureAwait(false);
        return next;
    }

    #endregion

    #region 4B. CompleteInvocationAndStartNextRoundAsync

    public async Task<MeetingInvocationRecord> CompleteInvocationAndStartNextRoundAsync(
        string invocationId,
        string targetStatus,
        string? messageId,
        string? errorCode,
        string? errorMessage,
        int nextRoundIndex,
        MeetingInvocationScheduleInput firstInvocation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(firstInvocation);
        if (nextRoundIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextRoundIndex), "Next round index must be positive.");
        }
        if (targetStatus is not ("Completed" or "Skipped"))
        {
            throw new InvalidOperationException(
                $"Target status '{targetStatus}' is not valid; expected Completed or Skipped.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        var context = await LoadInvocationContextAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);

        if (nextRoundIndex != context.RoundIndex + 1)
        {
            throw new InvalidOperationException(
                $"Next round index {nextRoundIndex} does not follow current round {context.RoundIndex}.");
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_invocations
                SET status = $target,
                    message_id = $messageId,
                    error_code = $errorCode,
                    error_message = $errorMessage,
                    completed_at = $now
                WHERE invocation_id = $invocationId
                  AND status = 'Running';
                """;
            cmd.Parameters.AddWithValue("$target", targetStatus);
            cmd.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$invocationId", invocationId);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' is missing or not in 'Running' state.");
            }
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_rounds
                SET status = 'Completed',
                    completed_at = $now
                WHERE session_id = $sessionId
                  AND round_index = $roundIndex
                  AND run_id = $runId
                  AND status = 'Running';
                """;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$sessionId", context.SessionId);
            cmd.Parameters.AddWithValue("$roundIndex", context.RoundIndex);
            cmd.Parameters.AddWithValue("$runId", context.RunId);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Round {context.RoundIndex} for session '{context.SessionId}' is not in 'Running' state.");
            }
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET current_round = $nextRound,
                    updated_at = $now
                WHERE session_id = $sessionId
                  AND run_id = $runId
                  AND current_round = $currentRound
                  AND status = 'Running';
                """;
            cmd.Parameters.AddWithValue("$nextRound", nextRoundIndex);
            cmd.Parameters.AddWithValue("$runId", context.RunId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$sessionId", context.SessionId);
            cmd.Parameters.AddWithValue("$currentRound", context.RoundIndex);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{context.SessionId}' round conflict: expected {context.RoundIndex}.");
            }
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO meeting_rounds(
                    session_id, round_index, run_id, status,
                    first_invocation_id, created_at, started_at)
                VALUES(
                    $sessionId, $roundIndex, $runId, 'Running',
                    $firstInvocationId, $createdAt, $startedAt);
                """;
            cmd.Parameters.AddWithValue("$sessionId", context.SessionId);
            cmd.Parameters.AddWithValue("$roundIndex", nextRoundIndex);
            cmd.Parameters.AddWithValue("$runId", context.RunId);
            cmd.Parameters.AddWithValue("$firstInvocationId", firstInvocation.InvocationId);
            cmd.Parameters.AddWithValue("$createdAt", now);
            cmd.Parameters.AddWithValue("$startedAt", now);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Failed to insert round {nextRoundIndex} for session '{context.SessionId}'.");
            }
        }

        var scheduled = await InsertScheduledInvocationAsync(
            context.SessionId, context.RunId, nextRoundIndex, 0,
            firstInvocation, now, transaction, ct)
            .ConfigureAwait(false);

        transaction.Commit();
        await InjectFailureAsync(SqliteMeetingRepositoryFailurePoint.AfterInvocationCompletionCommit)
            .ConfigureAwait(false);
        return scheduled;
    }

    #endregion

    #region 4A. CompleteRoundAndMeetingAsync

    public async Task CompleteRoundAndMeetingAsync(
        string sessionId,
        int roundIndex,
        string meetingStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (roundIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundIndex), "Round index must be positive.");
        }
        if (meetingStatus is not ("Completed" or "Failed" or "Cancelled"))
        {
            throw new InvalidOperationException(
                $"Meeting status '{meetingStatus}' is not valid; expected Completed, Failed, or Cancelled.");
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_rounds
                SET status = $target,
                    completed_at = $now
                WHERE session_id = $sessionId
                  AND round_index = $roundIndex
                  AND status = 'Running';
                """;
            cmd.Parameters.AddWithValue("$target", meetingStatus);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$roundIndex", roundIndex);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"No running round {roundIndex} found for session '{sessionId}'.");
            }
        }

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET status = $target,
                    updated_at = $now
                WHERE session_id = $sessionId
                  AND current_round = $roundIndex;
                """;
            cmd.Parameters.AddWithValue("$target", meetingStatus);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$roundIndex", roundIndex);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{sessionId}' not at expected round {roundIndex}.");
            }
        }

        transaction.Commit();
    }

    #endregion

    #region 5. TryUpdateSelectionAsync

    public async Task<bool> TryUpdateSelectionAsync(
        string sessionId,
        int expectedSelectionVersion,
        int newSelectionVersion,
        string policyJson,
        string policyHash,
        IReadOnlyList<MeetingParticipantInput> participants,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(policyJson);
        ArgumentNullException.ThrowIfNull(policyHash);
        ArgumentNullException.ThrowIfNull(participants);
        if (newSelectionVersion <= expectedSelectionVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newSelectionVersion),
                "The new selection version must be greater than the expected version.");
        }
        ValidateParticipants(participants);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET selection_version = $newVersion,
                    policy_json = $policyJson,
                    policy_hash = $policyHash,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND selection_version = $expectedVersion;
                """;
            cmd.Parameters.AddWithValue("$newVersion", newSelectionVersion);
            cmd.Parameters.AddWithValue("$policyJson", policyJson);
            cmd.Parameters.AddWithValue("$policyHash", policyHash);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$expectedVersion", expectedSelectionVersion);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                transaction.Commit();
                return false;
            }
        }

        var ordered = participants
            .OrderBy(static p => p.JoinOrder)
            .ThenBy(static p => p.ParticipantId, StringComparer.Ordinal)
            .ToArray();

        foreach (var p in ordered)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO meeting_participants(
                    session_id, participant_id, agent_id, agent_ref_json,
                    display_name, join_order, status, joined_selection_version,
                    joined_at, updated_at)
                VALUES(
                    $sessionId, $participantId, $agentId, $agentRefJson,
                    $displayName, $joinOrder, $status, $newVersion,
                    $now, $now)
                ON CONFLICT(session_id, participant_id) DO UPDATE SET
                    agent_id = excluded.agent_id,
                    agent_ref_json = excluded.agent_ref_json,
                    display_name = excluded.display_name,
                    join_order = excluded.join_order,
                    status = excluded.status,
                    removed_selection_version = NULL,
                    removed_at = NULL,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$participantId", p.ParticipantId);
            cmd.Parameters.AddWithValue("$agentId", p.AgentId);
            cmd.Parameters.AddWithValue("$agentRefJson", (object?)p.AgentRefJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$displayName", (object?)p.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$joinOrder", p.JoinOrder);
            cmd.Parameters.AddWithValue("$status", p.Status);
            cmd.Parameters.AddWithValue("$newVersion", newSelectionVersion);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var participantIds = ordered
            .Select(static p => p.ParticipantId)
            .ToArray();

        await using (var softDelete = _connection.CreateCommand())
        {
            softDelete.Transaction = transaction;
            var participantParameters = participantIds
                .Select(static (_, index) => $"$participant{index}")
                .ToArray();
            var excludedParticipants = participantParameters.Length == 0
                ? string.Empty
                : $" AND participant_id NOT IN ({string.Join(", ", participantParameters)})";
            softDelete.CommandText = $"""
                UPDATE meeting_participants
                SET status = 'removed',
                    removed_selection_version = $newVersion,
                    removed_at = $now,
                    updated_at = $now
                WHERE session_id = $sessionId
                  AND status IN ('active', 'standby')
                  {excludedParticipants};
                """;
            softDelete.Parameters.AddWithValue("$newVersion", newSelectionVersion);
            softDelete.Parameters.AddWithValue("$now", now);
            softDelete.Parameters.AddWithValue("$sessionId", sessionId);
            for (var index = 0; index < participantIds.Length; index++)
            {
                softDelete.Parameters.AddWithValue(
                    participantParameters[index],
                    participantIds[index]);
            }

            await softDelete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    #endregion

    #region 6. SavePendingApprovalAsync

    public async Task<bool> SavePendingApprovalAsync(
        string sessionId,
        string approvalRequestId,
        string approvalJson,
        string status,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);
        ArgumentNullException.ThrowIfNull(approvalJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        string? existingId = null;
        string? existingJson = null;
        string? existingStatus = null;

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                SELECT pending_approval_request_id, pending_approval_json, pending_approval_status
                FROM meeting_sessions
                WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Meeting session '{sessionId}' not found.");
            }

            existingId = reader.IsDBNull(0) ? null : reader.GetString(0);
            existingJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            existingStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        if (existingId is not null)
        {
            if (string.Equals(existingStatus, "Decided", StringComparison.Ordinal))
            {
                if (string.Equals(existingId, approvalRequestId, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return true;
                }

            }
            else
            {
                if (string.Equals(existingId, approvalRequestId, StringComparison.Ordinal)
                    && string.Equals(existingJson, approvalJson, StringComparison.Ordinal)
                    && string.Equals(existingStatus, status, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return true;
                }

                throw new InvalidOperationException(
                    $"An active pending approval '{existingId}' already exists for session '{sessionId}' and cannot be overwritten.");
            }
        }

        await using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE meeting_sessions
                SET pending_approval_request_id = $approvalRequestId,
                    pending_approval_json = $approvalJson,
                    pending_approval_status = $status,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId;
                """;
            update.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
            update.Parameters.AddWithValue("$approvalJson", approvalJson);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$updatedAt", now);
            update.Parameters.AddWithValue("$sessionId", sessionId);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{sessionId}' not found during update.");
            }
        }

        transaction.Commit();
        return true;
    }

    public async Task ResolvePendingApprovalAsync(
        string sessionId,
        string approvalRequestId,
        string finalJson,
        string finalStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);
        ArgumentNullException.ThrowIfNull(finalJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalStatus);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET pending_approval_json = $finalJson,
                    pending_approval_status = $finalStatus,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND pending_approval_request_id = $approvalRequestId
                  AND pending_approval_status != 'Decided';
                """;
            cmd.Parameters.AddWithValue("$finalJson", finalJson);
            cmd.Parameters.AddWithValue("$finalStatus", finalStatus);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return;
            }
        }

        await using (var check = _connection.CreateCommand())
        {
            check.CommandText = """
                SELECT pending_approval_request_id, pending_approval_status, pending_approval_json
                FROM meeting_sessions
                WHERE session_id = $sessionId;
                """;
            check.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var storedId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var storedStatus = reader.IsDBNull(1) ? null : reader.GetString(1);
                var storedJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (string.Equals(storedId, approvalRequestId, StringComparison.Ordinal)
                    && string.Equals(storedJson, finalJson, StringComparison.Ordinal)
                    && string.Equals(storedStatus, finalStatus, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            $"Pending approval '{approvalRequestId}' is not active for session '{sessionId}'.");
    }

    public async Task<MeetingApprovalRecord?> FindApprovalByRequestIdAsync(
        string approvalRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, run_id, pending_approval_request_id,
                   pending_approval_json, pending_approval_status
            FROM meeting_sessions
            WHERE pending_approval_request_id = $approvalRequestId;
            """;
        cmd.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new MeetingApprovalRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<IReadOnlyList<MeetingApprovalRecord>> ListRecoverableApprovalsAsync(
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, run_id, pending_approval_request_id,
                   pending_approval_json, pending_approval_status
            FROM meeting_sessions
            WHERE status = 'WaitingForApproval'
              AND pending_approval_status IN ('Pending', 'Decided')
              AND pending_approval_request_id IS NOT NULL
            ORDER BY updated_at, session_id;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var approvals = new List<MeetingApprovalRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            approvals.Add(new MeetingApprovalRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return approvals;
    }

    public async Task<bool> TryTransitionMeetingStatusAsync(
        string sessionId,
        string expectedStatus,
        string targetStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStatus);

        if (string.Equals(expectedStatus, targetStatus, StringComparison.Ordinal))
        {
            return true;
        }

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET status = $targetStatus,
                    updated_at = $updatedAt
                WHERE session_id = $sessionId
                  AND status = $expectedStatus;
                """;
            cmd.Parameters.AddWithValue("$targetStatus", targetStatus);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$expectedStatus", expectedStatus);
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected == 1)
            {
                transaction.Commit();
                return true;
            }
        }

        await using (var check = _connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT status
                FROM meeting_sessions
                WHERE session_id = $sessionId;
                """;
            check.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Meeting session '{sessionId}' not found.");
            }

            var currentStatus = reader.GetString(0);
            if (string.Equals(currentStatus, targetStatus, StringComparison.Ordinal))
            {
                transaction.Commit();
                return true;
            }
        }

        transaction.Commit();
        return false;
    }

    #endregion

    #region 7. Round summaries

    public async Task<MeetingRoundRecord?> GetRoundSummaryAsync(
        string sessionId,
        int roundIndex,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, round_index, run_id, status,
                   first_invocation_id, summary_message_id,
                   summarizes_through_seq, summary_policy_hash,
                   summary_agent_id, summary_invocation_id,
                   created_at, started_at, completed_at
            FROM meeting_rounds
            WHERE session_id = $sessionId
              AND round_index = $roundIndex
              AND summary_message_id IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$roundIndex", roundIndex);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadMeetingRound(reader)
            : null;
    }

    public async Task SaveRoundSummaryAsync(
        string sessionId,
        int roundIndex,
        long summarizesThroughSeq,
        string summaryPolicyHash,
        string summaryMessageId,
        string? summaryAgentId,
        string? summaryInvocationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryPolicyHash);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        await SaveRoundSummaryCoreAsync(
                sessionId,
                roundIndex,
                summarizesThroughSeq,
                summaryPolicyHash,
                summaryMessageId,
                summaryAgentId,
                summaryInvocationId,
                now,
                transaction,
                ct)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private async Task SaveRoundSummaryCoreAsync(
        string sessionId,
        int roundIndex,
        long summarizesThroughSeq,
        string summaryPolicyHash,
        string summaryMessageId,
        string? summaryAgentId,
        string? summaryInvocationId,
        string now,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE meeting_rounds
            SET summary_message_id = $messageId,
                summarizes_through_seq = $seq,
                summary_policy_hash = $policyHash,
                summary_agent_id = $agentId,
                summary_invocation_id = $invocationId,
                completed_at = $now
            WHERE session_id = $sessionId
              AND round_index = $roundIndex
              AND (
                    (summary_message_id IS NULL
                     AND summarizes_through_seq IS NULL
                     AND summary_policy_hash IS NULL
                     AND summary_agent_id IS NULL
                     AND summary_invocation_id IS NULL)
                    OR
                    (summary_message_id = $messageId
                     AND summarizes_through_seq = $seq
                     AND summary_policy_hash = $policyHash
                     AND ((summary_agent_id IS NULL AND $agentId IS NULL)
                          OR summary_agent_id = $agentId)
                     AND ((summary_invocation_id IS NULL AND $invocationId IS NULL)
                          OR summary_invocation_id = $invocationId))
                  );
            """;
        cmd.Parameters.AddWithValue("$messageId", summaryMessageId);
        cmd.Parameters.AddWithValue("$seq", summarizesThroughSeq);
        cmd.Parameters.AddWithValue("$policyHash", summaryPolicyHash);
        cmd.Parameters.AddWithValue("$agentId", (object?)summaryAgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$invocationId", (object?)summaryInvocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$roundIndex", roundIndex);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            await using var check = _connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = """
                SELECT summary_message_id, summarizes_through_seq, summary_policy_hash,
                       summary_agent_id, summary_invocation_id
                FROM meeting_rounds
                WHERE session_id = $sessionId AND round_index = $roundIndex;
                """;
            check.Parameters.AddWithValue("$sessionId", sessionId);
            check.Parameters.AddWithValue("$roundIndex", roundIndex);
            await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var existingId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var existingSeq = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var existingHash = reader.IsDBNull(2) ? null : reader.GetString(2);
                var existingAgentId = reader.IsDBNull(3) ? null : reader.GetString(3);
                var existingInvocationId = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (string.Equals(existingId, summaryMessageId, StringComparison.Ordinal)
                    && existingSeq == summarizesThroughSeq
                    && string.Equals(existingHash, summaryPolicyHash, StringComparison.Ordinal)
                    && string.Equals(existingAgentId, summaryAgentId, StringComparison.Ordinal)
                    && string.Equals(existingInvocationId, summaryInvocationId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Round summary for session '{sessionId}' round {roundIndex} already has a different scope.");
        }
    }

    #endregion

    #region 8. GetRunFirstRoundIndexAsync / GetMeetingSnapshotAsync

    /// <summary>Gets the first absolute round index assigned to a Meeting Run.</summary>
    public async Task<int?> GetRunFirstRoundIndexAsync(
        string sessionId,
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(round_index)
            FROM meeting_rounds
            WHERE session_id = $sessionId
              AND run_id = $runId;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$runId", runId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<MeetingSnapshot?> GetMeetingSnapshotAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        MeetingSessionRecord? session = null;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, run_id, status, current_round,
                       selector_state, policy_json, policy_hash, selection_version,
                       pending_approval_request_id, pending_approval_json, pending_approval_status,
                       created_at, updated_at
                FROM meeting_sessions
                WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                session = ReadMeetingSession(reader);
            }
        }

        if (session is null)
        {
            return null;
        }

        var participants = new List<MeetingParticipantRecord>();
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, participant_id, agent_id, agent_ref_json,
                       display_name, join_order, status,
                       joined_selection_version, removed_selection_version,
                       joined_at, removed_at, updated_at
                FROM meeting_participants
                WHERE session_id = $sessionId
                ORDER BY CASE WHEN status = 'removed' THEN 1 ELSE 0 END,
                         join_order, participant_id;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                participants.Add(ReadMeetingParticipant(reader));
            }
        }

        MeetingRoundRecord? currentRound = null;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, round_index, run_id, status,
                       first_invocation_id, summary_message_id,
                       summarizes_through_seq, summary_policy_hash,
                       summary_agent_id, summary_invocation_id,
                       created_at, started_at, completed_at
                FROM meeting_rounds
                WHERE session_id = $sessionId
                ORDER BY round_index DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                currentRound = ReadMeetingRound(reader);
            }
        }

        MeetingInvocationRecord? nextScheduled = null;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT invocation_id, session_id, run_id, round_index, ordinal,
                       participant_id, agent_id, role, status,
                       selection_version, retry_count, message_id,
                       selector_decision_json, scheduled_at, started_at,
                       completed_at, error_code, error_message
                FROM meeting_invocations
                WHERE session_id = $sessionId
                  AND status IN ('Scheduled', 'Interrupted')
                ORDER BY round_index, ordinal
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                nextScheduled = ReadMeetingInvocation(reader);
            }
        }

        MeetingRoundRecord? latestSummary = null;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, round_index, run_id, status,
                       first_invocation_id, summary_message_id,
                       summarizes_through_seq, summary_policy_hash,
                       summary_agent_id, summary_invocation_id,
                       created_at, started_at, completed_at
                FROM meeting_rounds
                WHERE session_id = $sessionId
                  AND summary_message_id IS NOT NULL
                ORDER BY round_index DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                latestSummary = ReadMeetingRound(reader);
            }
        }

        return new MeetingSnapshot(
            session,
            participants,
            currentRound,
            nextScheduled,
            session.PendingApprovalJson,
            session.PendingApprovalStatus,
            latestSummary);
    }

    #endregion

    #region 8B. GetInvocationContextMapAsync

    public async Task<IReadOnlyDictionary<string, MeetingInvocationContextRecord>> GetInvocationContextMapAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT invocation_id, round_index, role
            FROM meeting_invocations
            WHERE session_id = $sessionId
            ORDER BY round_index, ordinal;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        var map = new Dictionary<string, MeetingInvocationContextRecord>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var invocationId = reader.GetString(0);
            var roundIndex = reader.GetInt32(1);
            var rawRole = reader.IsDBNull(2) ? null : reader.GetString(2);
            var role = NormalizeInvocationRole(rawRole, invocationId);
            map[invocationId] = new MeetingInvocationContextRecord(invocationId, roundIndex, role);
        }

        return map;
    }

    private static string? NormalizeInvocationRole(string? rawRole, string invocationId)
    {
        if (string.IsNullOrWhiteSpace(rawRole))
        {
            return null;
        }

        if (rawRole.Equals("participant", StringComparison.OrdinalIgnoreCase))
        {
            return "participant";
        }

        if (rawRole.Equals("selector", StringComparison.OrdinalIgnoreCase))
        {
            return "selector";
        }

        if (rawRole.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }

        if (rawRole.Equals("summarizer", StringComparison.OrdinalIgnoreCase))
        {
            return "summarizer";
        }

        throw new InvalidOperationException(
            $"Unknown role '{rawRole}' for invocation '{invocationId}'.");
    }

    #endregion

    #region 9. RecoverRunningInvocationsAsync

    public async Task<IReadOnlyList<MeetingInvocationRecoveryItem>> RecoverRunningInvocationsAsync(
        CancellationToken ct = default)
    {
        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);

        var items = new List<MeetingInvocationRecoveryItem>();
        await using (var select = _connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT invocation_id, session_id, run_id, round_index,
                       ordinal, status, participant_id, agent_id,
                       role, selection_version, retry_count
                FROM meeting_invocations
                WHERE status = 'Running';
                """;
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new MeetingInvocationRecoveryItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    "Interrupted",
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10)));
            }
        }

        if (items.Count > 0)
        {
            await using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE meeting_invocations
                SET status = 'Interrupted',
                    completed_at = $now
                WHERE status = 'Running';
                """;
            update.Parameters.AddWithValue("$now", now);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return items;
    }

    #endregion

    #region 10. ResumeRecoverableInvocationAsync

    /// <summary>
    /// Explicitly resumes a recoverable Meeting Invocation and its original Run in one transaction.
    /// </summary>
    public async Task<MeetingInvocationRecord> ResumeRecoverableInvocationAsync(
        string invocationId,
        int maxRetryCount,
        bool resumeWithoutProvider = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetryCount);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        var invocation = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);

        if (string.Equals(invocation.Status, "Running", StringComparison.Ordinal))
        {
            var runningStatus = await LoadRunStatusAsync(invocation.RunId, transaction, ct)
                .ConfigureAwait(false);
            if (!string.Equals(runningStatus, "Running", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' is Running while Run '{invocation.RunId}' is '{runningStatus}'.");
            }

            transaction.Commit();
            return invocation;
        }

        if (invocation.Status is not ("Scheduled" or "Interrupted"))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is in state '{invocation.Status}' and cannot be resumed.");
        }

        if (!resumeWithoutProvider
            && string.Equals(invocation.Status, "Interrupted", StringComparison.Ordinal)
            && invocation.RetryCount >= maxRetryCount)
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' exhausted its recovery retry limit of {maxRetryCount}.");
        }

        await ValidateRecoverableMeetingAsync(invocation, transaction, ct).ConfigureAwait(false);

        await using (var resumeRun = _connection.CreateCommand())
        {
            resumeRun.Transaction = transaction;
            resumeRun.CommandText = """
                UPDATE runs
                SET status = 'Running',
                    terminal_text = NULL,
                    updated_at = $now
                WHERE run_id = $runId
                  AND session_id = $sessionId
                  AND status = 'Interrupted';
                """;
            resumeRun.Parameters.AddWithValue("$now", now);
            resumeRun.Parameters.AddWithValue("$runId", invocation.RunId);
            resumeRun.Parameters.AddWithValue("$sessionId", invocation.SessionId);
            if (await resumeRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                var runStatus = await LoadRunStatusAsync(invocation.RunId, transaction, ct)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Run '{invocation.RunId}' is in state '{runStatus}' and cannot resume Invocation '{invocationId}'.");
            }
        }

        await using (var resumeInvocation = _connection.CreateCommand())
        {
            resumeInvocation.Transaction = transaction;
            resumeInvocation.CommandText = """
                UPDATE meeting_invocations
                SET status = 'Running',
                    retry_count = retry_count + CASE
                        WHEN status = 'Interrupted' AND $incrementRetry = 1 THEN 1
                        ELSE 0
                    END,
                    started_at = COALESCE(started_at, $now),
                    completed_at = NULL,
                    error_code = NULL,
                    error_message = NULL
                WHERE invocation_id = $invocationId
                  AND status = $expectedStatus;
                """;
            resumeInvocation.Parameters.AddWithValue("$now", now);
            resumeInvocation.Parameters.AddWithValue("$invocationId", invocationId);
            resumeInvocation.Parameters.AddWithValue("$expectedStatus", invocation.Status);
            resumeInvocation.Parameters.AddWithValue("$incrementRetry", resumeWithoutProvider ? 0 : 1);
            if (await resumeInvocation.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' changed while its Run was being resumed.");
            }
        }

        var resumed = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);
        transaction.Commit();
        return resumed;
    }

    /// <summary>Pauses a failed Invocation, its Meeting, and its Run atomically.</summary>
    public async Task<MeetingInvocationRecord> PauseInvocationAsync(
        string invocationId,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        var invocation = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);
        if (!string.Equals(invocation.Status, "Running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is in state '{invocation.Status}' and cannot be paused.");
        }

        await using (var pauseInvocation = _connection.CreateCommand())
        {
            pauseInvocation.Transaction = transaction;
            pauseInvocation.CommandText = """
                UPDATE meeting_invocations
                SET status = 'Interrupted',
                    error_code = $errorCode,
                    error_message = $errorMessage,
                    completed_at = $now
                WHERE invocation_id = $invocationId
                  AND status = 'Running';
                """;
            pauseInvocation.Parameters.AddWithValue("$errorCode", errorCode);
            pauseInvocation.Parameters.AddWithValue("$errorMessage", errorMessage);
            pauseInvocation.Parameters.AddWithValue("$now", now);
            pauseInvocation.Parameters.AddWithValue("$invocationId", invocationId);
            if (await pauseInvocation.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' changed while it was being paused.");
            }
        }

        await using (var pauseMeeting = _connection.CreateCommand())
        {
            pauseMeeting.Transaction = transaction;
            pauseMeeting.CommandText = """
                UPDATE meeting_sessions
                SET status = 'Paused',
                    updated_at = $now
                WHERE session_id = $sessionId
                  AND run_id = $runId
                  AND current_round = $roundIndex
                  AND status = 'Running';
                """;
            pauseMeeting.Parameters.AddWithValue("$now", now);
            pauseMeeting.Parameters.AddWithValue("$sessionId", invocation.SessionId);
            pauseMeeting.Parameters.AddWithValue("$runId", invocation.RunId);
            pauseMeeting.Parameters.AddWithValue("$roundIndex", invocation.RoundIndex);
            if (await pauseMeeting.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{invocation.SessionId}' changed while Invocation '{invocationId}' was being paused.");
            }
        }

        await using (var pauseRun = _connection.CreateCommand())
        {
            pauseRun.Transaction = transaction;
            pauseRun.CommandText = """
                UPDATE runs
                SET status = 'WaitingForApproval',
                    updated_at = $now
                WHERE run_id = $runId
                  AND session_id = $sessionId
                  AND status = 'Running';
                """;
            pauseRun.Parameters.AddWithValue("$now", now);
            pauseRun.Parameters.AddWithValue("$runId", invocation.RunId);
            pauseRun.Parameters.AddWithValue("$sessionId", invocation.SessionId);
            if (await pauseRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Run '{invocation.RunId}' changed while Invocation '{invocationId}' was being paused.");
            }
        }

        var paused = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);
        transaction.Commit();
        return paused;
    }

    #endregion

    #region 11. QueryRecoverableInvocationsAsync / ReconcileByMessageIdAsync

    public async Task<IReadOnlyList<MeetingInvocationRecoveryItem>> QueryRecoverableInvocationsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT invocation_id, session_id, run_id, round_index,
                   ordinal, status, participant_id, agent_id,
                   role, selection_version, retry_count
            FROM meeting_invocations
            WHERE session_id = $sessionId
              AND status IN ('Scheduled', 'Interrupted')
            ORDER BY round_index, ordinal;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        var items = new List<MeetingInvocationRecoveryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new MeetingInvocationRecoveryItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(10)));
        }

        return items;
    }

    public async Task<IReadOnlyList<MeetingInvocationReconciliationItem>> ReconcileByMessageIdAsync(
        string messageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT invocation_id, session_id, run_id, round_index,
                   ordinal, status, message_id, agent_id
            FROM meeting_invocations
            WHERE message_id = $messageId
            ORDER BY round_index, ordinal;
            """;
        cmd.Parameters.AddWithValue("$messageId", messageId);
        var items = new List<MeetingInvocationReconciliationItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new MeetingInvocationReconciliationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return items;
    }

    /// <summary>
    /// Reconciles a canonical message that was durably appended before its Invocation completion committed.
    /// </summary>
    public async Task<MeetingInvocationRecord> ReconcileInterruptedInvocationAsync(
        string invocationId,
        string messageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        var reconciled = await ReconcileInterruptedInvocationCoreAsync(
                invocationId,
                messageId,
                now,
                transaction,
                ct)
            .ConfigureAwait(false);
        transaction.Commit();
        return reconciled;
    }

    /// <summary>
    /// Reconciles an interrupted Summarizer Invocation and its round summary metadata atomically.
    /// </summary>
    public async Task<MeetingInvocationRecord> ReconcileInterruptedSummaryAsync(
        string invocationId,
        string messageId,
        long summarizesThroughSeq,
        string summaryPolicyHash,
        string summaryAgentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentOutOfRangeException.ThrowIfNegative(summarizesThroughSeq);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryPolicyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryAgentId);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        using var transaction = _connection.BeginTransaction(deferred: false);
        var reconciled = await ReconcileInterruptedInvocationCoreAsync(
                invocationId,
                messageId,
                now,
                transaction,
                ct)
            .ConfigureAwait(false);
        if (!string.Equals(reconciled.Role, "summarizer", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reconciled.AgentId, summaryAgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is not the expected Summarizer '{summaryAgentId}'.");
        }

        await SaveRoundSummaryCoreAsync(
                reconciled.SessionId,
                reconciled.RoundIndex,
                summarizesThroughSeq,
                summaryPolicyHash,
                messageId,
                summaryAgentId,
                invocationId,
                now,
                transaction,
                ct)
            .ConfigureAwait(false);
        transaction.Commit();
        return reconciled;
    }

    private async Task<MeetingInvocationRecord> ReconcileInterruptedInvocationCoreAsync(
        string invocationId,
        string messageId,
        string now,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var existing = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);

        if (string.Equals(existing.Status, "Completed", StringComparison.Ordinal))
        {
            if (!string.Equals(existing.MessageId, messageId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' is already completed with a different message ID.");
            }

            return existing;
        }

        if (!string.Equals(existing.Status, "Interrupted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is in state '{existing.Status}' and cannot be reconciled.");
        }

        if (existing.MessageId is not null
            && !string.Equals(existing.MessageId, messageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is already bound to a different message ID.");
        }

        await using (var collision = _connection.CreateCommand())
        {
            collision.Transaction = transaction;
            collision.CommandText = """
                SELECT invocation_id
                FROM meeting_invocations
                WHERE message_id = $messageId
                  AND invocation_id <> $invocationId
                LIMIT 1;
                """;
            collision.Parameters.AddWithValue("$messageId", messageId);
            collision.Parameters.AddWithValue("$invocationId", invocationId);
            if (await collision.ExecuteScalarAsync(ct).ConfigureAwait(false) is string boundInvocationId)
            {
                throw new InvalidOperationException(
                    $"Message ID '{messageId}' is already bound to Invocation '{boundInvocationId}'.");
            }
        }

        await using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE meeting_invocations
                SET status = 'Completed',
                    message_id = $messageId,
                    error_code = NULL,
                    error_message = NULL,
                    completed_at = $now
                WHERE invocation_id = $invocationId
                  AND status = 'Interrupted'
                  AND (message_id IS NULL OR message_id = $messageId);
                """;
            update.Parameters.AddWithValue("$messageId", messageId);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$invocationId", invocationId);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' changed while its canonical message was being reconciled.");
            }
        }

        var reconciled = await LoadInvocationAsync(invocationId, transaction, ct)
            .ConfigureAwait(false);
        return reconciled;
    }

    private async Task ValidateRecoverableMeetingAsync(
        MeetingInvocationRecord invocation,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT status, run_id, current_round
            FROM meeting_sessions
            WHERE session_id = $sessionId;
            """;
        cmd.Parameters.AddWithValue("$sessionId", invocation.SessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Meeting session '{invocation.SessionId}' was not found for Invocation '{invocation.InvocationId}'.");
        }

        var meetingStatus = reader.GetString(0);
        var meetingRunId = reader.GetString(1);
        var currentRound = reader.GetInt32(2);
        if (!string.Equals(meetingStatus, "Running", StringComparison.Ordinal)
            || !string.Equals(meetingRunId, invocation.RunId, StringComparison.Ordinal)
            || currentRound != invocation.RoundIndex)
        {
            throw new InvalidOperationException(
                $"Meeting session '{invocation.SessionId}' cannot resume Invocation '{invocation.InvocationId}': "
                + $"status='{meetingStatus}', runId='{meetingRunId}', currentRound={currentRound}.");
        }
    }

    private async Task<string> LoadRunStatusAsync(
        string runId,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT status FROM runs WHERE run_id = $runId;";
        cmd.Parameters.AddWithValue("$runId", runId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException($"Run '{runId}' was not found.");
    }

    #endregion

    #region Helpers

    private async Task<MeetingInvocationRecord> InsertScheduledInvocationAsync(
        string sessionId,
        string runId,
        int roundIndex,
        int ordinal,
        MeetingInvocationScheduleInput input,
        string now,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        int inserted;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO meeting_invocations(
                    invocation_id, session_id, run_id, round_index, ordinal,
                    participant_id, agent_id, role, status,
                    selection_version, retry_count, scheduled_at)
                VALUES(
                    $invocationId, $sessionId, $runId, $roundIndex, $ordinal,
                    $participantId, $agentId, $role, 'Scheduled',
                    $selectionVersion, 0, $scheduledAt);
                """;
            cmd.Parameters.AddWithValue("$invocationId", input.InvocationId);
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$roundIndex", roundIndex);
            cmd.Parameters.AddWithValue("$ordinal", ordinal);
            cmd.Parameters.AddWithValue("$participantId", (object?)input.ParticipantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$agentId", (object?)input.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$role", (object?)input.Role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$selectionVersion", input.SelectionVersion);
            cmd.Parameters.AddWithValue("$scheduledAt", now);
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var existing = await LoadInvocationAsync(input.InvocationId, transaction, ct)
            .ConfigureAwait(false);

        if (inserted == 0)
        {
            if (!string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(existing.RunId, runId, StringComparison.Ordinal)
                || existing.RoundIndex != roundIndex
                || existing.Ordinal != ordinal
                || !string.Equals(existing.ParticipantId, input.ParticipantId, StringComparison.Ordinal)
                || !string.Equals(existing.AgentId, input.AgentId, StringComparison.Ordinal)
                || !string.Equals(existing.Role, input.Role, StringComparison.Ordinal)
                || existing.SelectionVersion != input.SelectionVersion)
            {
                throw new InvalidOperationException(
                    $"Invocation '{input.InvocationId}' already exists with different immutable fields.");
            }
        }

        return existing;
    }

    private async Task<MeetingInvocationRecord> LoadInvocationAsync(
        string invocationId,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT invocation_id, session_id, run_id, round_index, ordinal,
                   participant_id, agent_id, role, status,
                   selection_version, retry_count, message_id,
                   selector_decision_json, scheduled_at, started_at,
                   completed_at, error_code, error_message
            FROM meeting_invocations
            WHERE invocation_id = $invocationId;
            """;
        cmd.Parameters.AddWithValue("$invocationId", invocationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' was not found after insert.");
        }

        return ReadMeetingInvocation(reader);
    }

    private async Task<(string SessionId, string RunId, int RoundIndex, int Ordinal)>
        LoadInvocationContextAsync(
            string invocationId,
            SqliteTransaction transaction,
            CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT session_id, run_id, round_index, ordinal
            FROM meeting_invocations
            WHERE invocation_id = $invocationId;
            """;
        cmd.Parameters.AddWithValue("$invocationId", invocationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Invocation '{invocationId}' not found.");
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static MeetingSessionRecord ReadMeetingSession(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3),
        r.IsDBNull(4) ? null : (byte[]?)r.GetValue(4),
        r.GetString(5),
        r.GetString(6),
        r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        ParseTimestamp(r.GetString(11)),
        ParseTimestamp(r.GetString(12)));

    private static MeetingRoundRecord ReadMeetingRound(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetInt32(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        ParseTimestamp(r.GetString(10)),
        r.IsDBNull(11) ? (DateTimeOffset?)null : ParseTimestamp(r.GetString(11)),
        r.IsDBNull(12) ? (DateTimeOffset?)null : ParseTimestamp(r.GetString(12)));

    private static MeetingParticipantRecord ReadMeetingParticipant(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetInt32(5),
        r.GetString(6),
        r.GetInt32(7),
        r.IsDBNull(8) ? (int?)null : r.GetInt32(8),
        ParseTimestamp(r.GetString(9)),
        r.IsDBNull(10) ? (DateTimeOffset?)null : ParseTimestamp(r.GetString(10)),
        ParseTimestamp(r.GetString(11)));

    private static MeetingInvocationRecord ReadMeetingInvocation(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3),
        r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetString(8),
        r.GetInt32(9),
        r.GetInt32(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        ParseTimestamp(r.GetString(13)),
        r.IsDBNull(14) ? (DateTimeOffset?)null : ParseTimestamp(r.GetString(14)),
        r.IsDBNull(15) ? (DateTimeOffset?)null : ParseTimestamp(r.GetString(15)),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetString(17));

    private static void ValidateParticipants(IReadOnlyList<MeetingParticipantInput> participants)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOrders = new HashSet<int>();
        foreach (var p in participants)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(p.ParticipantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(p.AgentId);
            if (!seenIds.Add(p.ParticipantId))
            {
                throw new ArgumentException(
                    $"Duplicate participant ID '{p.ParticipantId}'.", nameof(participants));
            }
            if (!seenOrders.Add(p.JoinOrder))
            {
                throw new ArgumentException(
                    $"Duplicate join order {p.JoinOrder}.", nameof(participants));
            }
            if (p.Status is not ("active" or "standby"))
            {
                throw new ArgumentException(
                    $"Invalid status '{p.Status}' for participant '{p.ParticipantId}'.",
                    nameof(participants));
            }
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    #endregion

    private ValueTask InjectFailureAsync(SqliteMeetingRepositoryFailurePoint point) =>
        _options.FailureInjector is { } injector
            ? injector(point, CancellationToken.None)
            : ValueTask.CompletedTask;
}
