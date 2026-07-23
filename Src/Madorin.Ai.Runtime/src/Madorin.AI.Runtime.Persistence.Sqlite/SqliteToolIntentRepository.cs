using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

public sealed class SqliteToolIntentRepository(SqliteConnection connection) : IToolStateStore, IDisposable
{
    private const string PendingStatus = "pending";
    private const string IntentSelectSql = """
        SELECT call_id,
               invocation_id,
               run_id,
               session_id,
               agent_id,
               parent_agent_id,
               tool_id,
               catalog_version,
               arguments_hash,
               status,
               grant_id,
               approval_request_id,
               result_json,
               result_hash,
               result_blob_id,
               result_blob_length,
               result_blob_sha256,
               result_blob_content_type,
               result_blob_access_scope,
               result_blob_expires_at,
               error_code,
               error_message,
               result_visible,
               created_at,
               sent_at,
               completed_at,
               work_step_id,
               plan_version
        FROM tool_intents
        """;
    private const string GrantSelectSql = """
        SELECT grant_id,
               run_id,
               agent_id,
               parent_grant_id,
               root_grant_id,
               workspace_root,
               allowed_tool_ids,
               allowed_call_ids,
               maximum_risk,
               allowed_read_roots,
               allowed_write_roots,
               allow_overwrite,
               allow_move,
               allow_delete,
               allowed_executables,
               allowed_environment_variables,
               allow_powershell,
               network_policy_json,
               expires_at,
               allow_delegation,
               delegated_agent_ids,
               delegation_chain,
               created_at,
               revoked_at,
               revocation_reason,
               approval_request_id
        FROM tool_grants
        """;

    private readonly SqliteConnection _connection = connection
        ?? throw new ArgumentNullException(nameof(connection));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> InsertPendingAsync(
        string callId,
        string invocationId,
        string runId,
        string sessionId,
        string toolId,
        string argumentsHash,
        CancellationToken ct = default)
    {
        return await TryCreateIntentAsync(
            new ToolIntentState(
                callId,
                invocationId,
                runId,
                sessionId,
                AgentId: string.Empty,
                ParentAgentId: null,
                toolId,
                ToolCatalogVersion: string.Empty,
                argumentsHash,
                ToolIntentStatus.Pending,
                GrantId: null,
                ApprovalRequestId: null,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                DateTimeOffset.UtcNow,
                SentAt: null,
                CompletedAt: null),
            ct).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(
        string callId,
        string status,
        string? resultJson = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (string.Equals(status, PendingStatus, StringComparison.Ordinal))
        {
            throw new ArgumentException("A tool intent cannot be completed as pending.", nameof(status));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE tool_intents
                SET status = $status,
                    result_json = $resultJson,
                    error_message = $errorMessage,
                    completed_at = $completedAt
                WHERE call_id = $callId
                  AND status = $pending;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$resultJson", (object?)resultJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$completedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$callId", callId);
            command.Parameters.AddWithValue("$pending", PendingStatus);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Tool intent '{callId}' is missing or is no longer pending.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolIntentRecord?> GetByCallIdAsync(
        string callId,
        CancellationToken ct = default)
    {
        var intent = await GetIntentAsync(callId, ct).ConfigureAwait(false);
        return intent is null
            ? null
            : new ToolIntentRecord(
                intent.CallId,
                intent.InvocationId,
                intent.RunId,
                intent.SessionId,
                intent.ToolId,
                intent.ArgumentsHash,
                FormatStatus(intent.Status),
                intent.ResultJson,
                intent.ErrorMessage,
                intent.CreatedAt,
                intent.CompletedAt,
                intent.AgentId,
                intent.ParentAgentId,
                intent.ToolCatalogVersion,
                intent.SentAt,
                intent.GrantId,
                intent.ApprovalRequestId,
                intent.ResultHash,
                intent.ResultBlob?.BlobId,
                intent.ErrorCode,
                intent.IsResultVisible,
                intent.WorkStepId,
                intent.PlanVersion);
    }

    public async Task<bool> TryCreateIntentAsync(
        ToolIntentState intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateIntentIdentity(intent);
        if (intent.Status is not ToolIntentStatus.Pending)
        {
            throw new ArgumentException("A new tool intent must be pending.", nameof(intent));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO tool_intents(
                    call_id,
                    invocation_id,
                    run_id,
                    session_id,
                    agent_id,
                    parent_agent_id,
                    tool_id,
                    catalog_version,
                    arguments_hash,
                    status,
                    grant_id,
                    approval_request_id,
                    result_json,
                    result_hash,
                    result_blob_id,
                    result_blob_length,
                    result_blob_sha256,
                    result_blob_content_type,
                    result_blob_access_scope,
                    result_blob_expires_at,
                    error_code,
                    error_message,
                    result_visible,
                    created_at,
                    sent_at,
                    completed_at,
                    work_step_id,
                    plan_version)
                VALUES(
                    $callId,
                    $invocationId,
                    $runId,
                    $sessionId,
                    $agentId,
                    $parentAgentId,
                    $toolId,
                    $catalogVersion,
                    $argumentsHash,
                    $status,
                    $grantId,
                    $approvalRequestId,
                    $resultJson,
                    $resultHash,
                    $resultBlobId,
                    $resultBlobLength,
                    $resultBlobSha256,
                    $resultBlobContentType,
                    $resultBlobAccessScope,
                    $resultBlobExpiresAt,
                    $errorCode,
                    $errorMessage,
                    $resultVisible,
                    $createdAt,
                    $sentAt,
                    $completedAt,
                    $workStepId,
                    $planVersion);
                """;
            BindIntent(command, intent);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolIntentState?> GetIntentAsync(
        string callId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {IntentSelectSql}
                WHERE call_id = $callId;
                """;
            command.Parameters.AddWithValue("$callId", callId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? ReadIntent(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolIntentState?> GetCompletedWorkStepIntentAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        string toolId,
        string argumentsHash,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsHash);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {IntentSelectSql}
                WHERE session_id = $sessionId
                  AND work_step_id = $workStepId
                  AND plan_version = $planVersion
                  AND tool_id = $toolId
                  AND arguments_hash = $argumentsHash
                  AND status IN ('succeeded', 'failed', 'cancelled', 'unknown')
                ORDER BY completed_at DESC, created_at DESC, call_id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$workStepId", workStepId);
            command.Parameters.AddWithValue("$planVersion", planVersion);
            command.Parameters.AddWithValue("$toolId", toolId);
            command.Parameters.AddWithValue("$argumentsHash", argumentsHash);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? ReadIntent(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ToolIntentState>> ListRecoverableIntentsAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {IntentSelectSql}
                WHERE status IN ('pending', 'sent', 'unknown')
                ORDER BY created_at, call_id;
                """;
            var intents = new List<ToolIntentState>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                intents.Add(ReadIntent(reader));
            }

            return intents;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ToolIntentState>> ListRecoverableWorkStepIntentsAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {IntentSelectSql}
                WHERE session_id = $sessionId
                  AND work_step_id = $workStepId
                  AND plan_version = $planVersion
                  AND status IN ('pending', 'sent', 'unknown')
                ORDER BY created_at, call_id;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$workStepId", workStepId);
            command.Parameters.AddWithValue("$planVersion", planVersion);
            var intents = new List<ToolIntentState>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                intents.Add(ReadIntent(reader));
            }

            return intents;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryMarkSentAsync(
        string callId,
        string grantId,
        string? approvalRequestId,
        DateTimeOffset sentAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE tool_intents
                SET status = 'sent',
                    grant_id = $grantId,
                    approval_request_id = $approvalRequestId,
                    sent_at = $sentAt
                WHERE call_id = $callId
                  AND status = 'pending';
                """;
            command.Parameters.AddWithValue("$callId", callId);
            command.Parameters.AddWithValue("$grantId", grantId);
            command.Parameters.AddWithValue(
                "$approvalRequestId",
                DbValue(approvalRequestId));
            command.Parameters.AddWithValue("$sentAt", FormatTimestamp(sentAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryCompleteIntentAsync(
        ToolIntentCompletion completion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.CallId);
        if (completion.Status is ToolIntentStatus.Pending or ToolIntentStatus.Sent)
        {
            throw new ArgumentException("A tool intent completion must be terminal or unknown.", nameof(completion));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE tool_intents
                SET status = $status,
                    result_json = $resultJson,
                    result_hash = $resultHash,
                    result_blob_id = $resultBlobId,
                    result_blob_length = $resultBlobLength,
                    result_blob_sha256 = $resultBlobSha256,
                    result_blob_content_type = $resultBlobContentType,
                    result_blob_access_scope = $resultBlobAccessScope,
                    result_blob_expires_at = $resultBlobExpiresAt,
                    error_code = $errorCode,
                    error_message = $errorMessage,
                    result_visible = $resultVisible,
                    completed_at = $completedAt
                WHERE call_id = $callId
                  AND status = $expectedStatus;
                """;
            command.Parameters.AddWithValue("$callId", completion.CallId);
            command.Parameters.AddWithValue("$expectedStatus", FormatStatus(completion.ExpectedStatus));
            command.Parameters.AddWithValue("$status", FormatStatus(completion.Status));
            command.Parameters.AddWithValue("$resultJson", DbValue(completion.ResultJson));
            command.Parameters.AddWithValue("$resultHash", DbValue(completion.ResultHash));
            BindResultBlob(command, completion.ResultBlob);
            command.Parameters.AddWithValue("$errorCode", DbValue(completion.ErrorCode));
            command.Parameters.AddWithValue("$errorMessage", DbValue(completion.ErrorMessage));
            command.Parameters.AddWithValue("$resultVisible", completion.IsResultVisible ? 1 : 0);
            command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completion.CompletedAt));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertGrantAsync(
        ToolGrantState grant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ValidateGrant(grant);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO tool_grants(
                    grant_id,
                    run_id,
                    agent_id,
                    parent_grant_id,
                    root_grant_id,
                    workspace_root,
                    allowed_tool_ids,
                    allowed_call_ids,
                    maximum_risk,
                    allowed_read_roots,
                    allowed_write_roots,
                    allow_overwrite,
                    allow_move,
                    allow_delete,
                    allowed_executables,
                    allowed_environment_variables,
                    allow_powershell,
                    network_policy_json,
                    expires_at,
                    allow_delegation,
                    delegated_agent_ids,
                    delegation_chain,
                    created_at,
                    revoked_at,
                    revocation_reason,
                    approval_request_id)
                VALUES(
                    $grantId,
                    $runId,
                    $agentId,
                    $parentGrantId,
                    $rootGrantId,
                    $workspaceRoot,
                    $allowedToolIds,
                    $allowedCallIds,
                    $maximumRisk,
                    $allowedReadRoots,
                    $allowedWriteRoots,
                    $allowOverwrite,
                    $allowMove,
                    $allowDelete,
                    $allowedExecutables,
                    $allowedEnvironmentVariables,
                    $allowPowerShell,
                    $networkPolicyJson,
                    $expiresAt,
                    $allowDelegation,
                    $delegatedAgentIds,
                    $delegationChain,
                    $createdAt,
                    $revokedAt,
                    $revocationReason,
                    $approvalRequestId);
                """;
            BindGrant(command, grant);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return;
            }

            var existing = await ReadGrantByIdCoreAsync(grant.GrantId, ct).ConfigureAwait(false);
            if (existing is null || !GrantEquivalent(existing, grant))
            {
                throw new InvalidOperationException(
                    $"Grant id '{grant.GrantId}' is already bound to different content.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolGrantState?> GetGrantAsync(
        string grantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadGrantByIdCoreAsync(grantId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ToolGrantState>> ListActiveGrantsAsync(
        string runId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"""
                {GrantSelectSql}
                WHERE run_id = $runId
                  AND revoked_at IS NULL
                  AND expires_at > $now
                ORDER BY created_at, grant_id;
                """;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            var grants = new List<ToolGrantState>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                grants.Add(ReadGrant(reader));
            }

            return grants;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RevokeGrantAsync(
        string grantId,
        bool revokeDescendants,
        DateTimeOffset revokedAt,
        string? reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = revokeDescendants
                ? """
                    WITH RECURSIVE descendants(grant_id) AS (
                        SELECT grant_id FROM tool_grants WHERE grant_id = $grantId
                        UNION ALL
                        SELECT child.grant_id
                        FROM tool_grants child
                        INNER JOIN descendants parent
                            ON child.parent_grant_id = parent.grant_id
                    )
                    UPDATE tool_grants
                    SET revoked_at = $revokedAt,
                        revocation_reason = $reason
                    WHERE grant_id IN (SELECT grant_id FROM descendants)
                      AND revoked_at IS NULL;
                    """
                : """
                    UPDATE tool_grants
                    SET revoked_at = $revokedAt,
                        revocation_reason = $reason
                    WHERE grant_id = $grantId
                      AND revoked_at IS NULL;
                    """;
            command.Parameters.AddWithValue("$grantId", grantId);
            command.Parameters.AddWithValue("$revokedAt", FormatTimestamp(revokedAt));
            command.Parameters.AddWithValue("$reason", DbValue(reason));
            var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            transaction.Commit();
            return affected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveApprovalAsync(
        ToolApprovalState approval,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ValidateApproval(approval);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tool_approvals(
                    approval_request_id,
                    call_id,
                    run_id,
                    agent_id,
                    tool_id,
                    arguments_hash,
                    decision,
                    grant_id,
                    reason,
                    requested_at,
                    decided_at)
                VALUES(
                    $approvalRequestId,
                    $callId,
                    $runId,
                    $agentId,
                    $toolId,
                    $argumentsHash,
                    $decision,
                    $grantId,
                    $reason,
                    $requestedAt,
                    $decidedAt)
                ON CONFLICT(approval_request_id) DO UPDATE SET
                    decision = excluded.decision,
                    grant_id = excluded.grant_id,
                    reason = excluded.reason,
                    decided_at = excluded.decided_at
                WHERE tool_approvals.call_id = excluded.call_id
                  AND tool_approvals.run_id = excluded.run_id
                  AND tool_approvals.agent_id = excluded.agent_id
                  AND tool_approvals.tool_id = excluded.tool_id
                  AND tool_approvals.arguments_hash = excluded.arguments_hash;
                """;
            command.Parameters.AddWithValue("$approvalRequestId", approval.ApprovalRequestId);
            command.Parameters.AddWithValue("$callId", approval.CallId);
            command.Parameters.AddWithValue("$runId", approval.RunId);
            command.Parameters.AddWithValue("$agentId", approval.AgentId);
            command.Parameters.AddWithValue("$toolId", approval.ToolId);
            command.Parameters.AddWithValue("$argumentsHash", approval.ArgumentsHash);
            command.Parameters.AddWithValue("$decision", approval.Decision);
            command.Parameters.AddWithValue("$grantId", DbValue(approval.GrantId));
            command.Parameters.AddWithValue("$reason", DbValue(approval.Reason));
            command.Parameters.AddWithValue("$requestedAt", FormatTimestamp(approval.RequestedAt));
            command.Parameters.AddWithValue(
                "$decidedAt",
                approval.DecidedAt is { } decidedAt
                    ? FormatTimestamp(decidedAt)
                    : DBNull.Value);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Approval id '{approval.ApprovalRequestId}' is bound to another request.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolApprovalState?> GetApprovalAsync(
        string approvalRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalRequestId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT approval_request_id,
                       call_id,
                       run_id,
                       agent_id,
                       tool_id,
                       arguments_hash,
                       decision,
                       grant_id,
                       reason,
                       requested_at,
                       decided_at
                FROM tool_approvals
                WHERE approval_request_id = $approvalRequestId;
                """;
            command.Parameters.AddWithValue("$approvalRequestId", approvalRequestId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ToolApprovalState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                ParseTimestamp(reader.GetString(9)),
                reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAuditAsync(
        ToolAuditRecord audit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ValidateAudit(audit);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tool_audit(
                    audit_id,
                    call_id,
                    run_id,
                    agent_id,
                    parent_agent_id,
                    tool_id,
                    catalog_version,
                    risk,
                    arguments_hash,
                    target_summary,
                    grant_id,
                    approval_request_id,
                    delegation_chain,
                    status,
                    result_hash,
                    diagnostic_id,
                    created_at,
                    completed_at,
                    duration_ms)
                VALUES(
                    $auditId,
                    $callId,
                    $runId,
                    $agentId,
                    $parentAgentId,
                    $toolId,
                    $catalogVersion,
                    $risk,
                    $argumentsHash,
                    $targetSummary,
                    $grantId,
                    $approvalRequestId,
                    $delegationChain,
                    $status,
                    $resultHash,
                    $diagnosticId,
                    $createdAt,
                    $completedAt,
                    $durationMilliseconds);
                """;
            command.Parameters.AddWithValue("$auditId", audit.AuditId);
            command.Parameters.AddWithValue("$callId", audit.CallId);
            command.Parameters.AddWithValue("$runId", audit.RunId);
            command.Parameters.AddWithValue("$agentId", audit.AgentId);
            command.Parameters.AddWithValue("$parentAgentId", DbValue(audit.ParentAgentId));
            command.Parameters.AddWithValue("$toolId", audit.ToolId);
            command.Parameters.AddWithValue("$catalogVersion", audit.ToolCatalogVersion);
            command.Parameters.AddWithValue("$risk", audit.Risk);
            command.Parameters.AddWithValue("$argumentsHash", audit.ArgumentsHash);
            command.Parameters.AddWithValue("$targetSummary", audit.TargetSummary);
            command.Parameters.AddWithValue("$grantId", DbValue(audit.GrantId));
            command.Parameters.AddWithValue("$approvalRequestId", DbValue(audit.ApprovalRequestId));
            command.Parameters.AddWithValue("$delegationChain", SerializeStrings(audit.DelegationChain));
            command.Parameters.AddWithValue("$status", audit.Status);
            command.Parameters.AddWithValue("$resultHash", DbValue(audit.ResultHash));
            command.Parameters.AddWithValue("$diagnosticId", audit.DiagnosticId);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(audit.CreatedAt));
            command.Parameters.AddWithValue(
                "$completedAt",
                audit.CompletedAt is { } completedAt
                    ? FormatTimestamp(completedAt)
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$durationMilliseconds",
                audit.DurationMilliseconds is { } durationMilliseconds
                    ? durationMilliseconds
                    : DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAuditAsync(
        string auditId,
        string status,
        string? resultHash,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE tool_audit
                SET status = $status,
                    result_hash = $resultHash,
                    completed_at = $completedAt,
                    duration_ms = MAX(
                        0,
                        CAST(ROUND(
                            (julianday($completedAt) - julianday(created_at)) * 86400000.0)
                            AS INTEGER))
                WHERE audit_id = $auditId
                  AND completed_at IS NULL;
                """;
            command.Parameters.AddWithValue("$auditId", auditId);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$resultHash", DbValue(resultHash));
            command.Parameters.AddWithValue("$completedAt", FormatTimestamp(completedAt));
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Tool audit '{auditId}' is missing or already complete.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ToolGrantState?> ReadGrantByIdCoreAsync(
        string grantId,
        CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"""
            {GrantSelectSql}
            WHERE grant_id = $grantId;
            """;
        command.Parameters.AddWithValue("$grantId", grantId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadGrant(reader)
            : null;
    }

    private static void BindIntent(SqliteCommand command, ToolIntentState intent)
    {
        command.Parameters.AddWithValue("$callId", intent.CallId);
        command.Parameters.AddWithValue("$invocationId", intent.InvocationId);
        command.Parameters.AddWithValue("$runId", intent.RunId);
        command.Parameters.AddWithValue("$sessionId", intent.SessionId);
        command.Parameters.AddWithValue("$agentId", DbValue(intent.AgentId));
        command.Parameters.AddWithValue("$parentAgentId", DbValue(intent.ParentAgentId));
        command.Parameters.AddWithValue("$toolId", intent.ToolId);
        command.Parameters.AddWithValue("$catalogVersion", DbValue(intent.ToolCatalogVersion));
        command.Parameters.AddWithValue("$argumentsHash", intent.ArgumentsHash);
        command.Parameters.AddWithValue("$status", FormatStatus(intent.Status));
        command.Parameters.AddWithValue("$grantId", DbValue(intent.GrantId));
        command.Parameters.AddWithValue("$approvalRequestId", DbValue(intent.ApprovalRequestId));
        command.Parameters.AddWithValue("$resultJson", DbValue(intent.ResultJson));
        command.Parameters.AddWithValue("$resultHash", DbValue(intent.ResultHash));
        BindResultBlob(command, intent.ResultBlob);
        command.Parameters.AddWithValue("$errorCode", DbValue(intent.ErrorCode));
        command.Parameters.AddWithValue("$errorMessage", DbValue(intent.ErrorMessage));
        command.Parameters.AddWithValue("$resultVisible", intent.IsResultVisible ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(intent.CreatedAt));
        command.Parameters.AddWithValue(
            "$sentAt",
            intent.SentAt is { } sentAt ? FormatTimestamp(sentAt) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$completedAt",
            intent.CompletedAt is { } completedAt ? FormatTimestamp(completedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$workStepId", DbValue(intent.WorkStepId));
        command.Parameters.AddWithValue("$planVersion", DbValue(intent.PlanVersion));
    }

    private static ToolIntentState ReadIntent(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            GetNullableString(reader, 4) ?? string.Empty,
            GetNullableString(reader, 5),
            reader.GetString(6),
            GetNullableString(reader, 7) ?? string.Empty,
            reader.GetString(8),
            ParseStatus(reader.GetString(9)),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            GetNullableString(reader, 12),
            GetNullableString(reader, 13),
            ReadResultBlob(reader),
            GetNullableString(reader, 20),
            GetNullableString(reader, 21),
            reader.GetInt64(22) != 0,
            ParseTimestamp(reader.GetString(23)),
            reader.IsDBNull(24) ? null : ParseTimestamp(reader.GetString(24)),
            reader.IsDBNull(25) ? null : ParseTimestamp(reader.GetString(25)),
            GetNullableString(reader, 26),
            GetNullableString(reader, 27));

    private static void BindResultBlob(
        SqliteCommand command,
        ToolResultBlobState? resultBlob)
    {
        command.Parameters.AddWithValue("$resultBlobId", DbValue(resultBlob?.BlobId));
        command.Parameters.AddWithValue(
            "$resultBlobLength",
            (object?)resultBlob?.Length ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultBlobSha256", DbValue(resultBlob?.Sha256));
        command.Parameters.AddWithValue("$resultBlobContentType", DbValue(resultBlob?.ContentType));
        command.Parameters.AddWithValue("$resultBlobAccessScope", DbValue(resultBlob?.AccessScope));
        command.Parameters.AddWithValue(
            "$resultBlobExpiresAt",
            resultBlob is null ? DBNull.Value : FormatTimestamp(resultBlob.ExpiresAt));
    }

    private static ToolResultBlobState? ReadResultBlob(SqliteDataReader reader)
    {
        var blobId = GetNullableString(reader, 14);
        if (blobId is null)
        {
            return null;
        }

        if (reader.IsDBNull(15)
            || GetNullableString(reader, 16) is not { } sha256
            || GetNullableString(reader, 17) is not { } contentType
            || GetNullableString(reader, 18) is not { } accessScope
            || GetNullableString(reader, 19) is not { } expiresAt)
        {
            throw new InvalidDataException(
                $"Tool result Blob '{blobId}' has incomplete persisted metadata.");
        }

        return new ToolResultBlobState(
            blobId,
            reader.GetInt64(15),
            sha256,
            contentType,
            accessScope,
            ParseTimestamp(expiresAt));
    }

    private static void BindGrant(SqliteCommand command, ToolGrantState grant)
    {
        command.Parameters.AddWithValue("$grantId", grant.GrantId);
        command.Parameters.AddWithValue("$runId", grant.RunId);
        command.Parameters.AddWithValue("$agentId", DbValue(grant.AgentId));
        command.Parameters.AddWithValue("$parentGrantId", DbValue(grant.ParentGrantId));
        command.Parameters.AddWithValue("$rootGrantId", grant.RootGrantId);
        command.Parameters.AddWithValue("$workspaceRoot", grant.WorkspaceRoot);
        command.Parameters.AddWithValue("$allowedToolIds", SerializeStrings(grant.AllowedToolIds));
        command.Parameters.AddWithValue("$allowedCallIds", SerializeStrings(grant.AllowedCallIds));
        command.Parameters.AddWithValue("$maximumRisk", grant.MaximumRisk);
        command.Parameters.AddWithValue("$allowedReadRoots", SerializeStrings(grant.AllowedReadRoots));
        command.Parameters.AddWithValue("$allowedWriteRoots", SerializeStrings(grant.AllowedWriteRoots));
        command.Parameters.AddWithValue("$allowOverwrite", grant.AllowOverwrite ? 1 : 0);
        command.Parameters.AddWithValue("$allowMove", grant.AllowMove ? 1 : 0);
        command.Parameters.AddWithValue("$allowDelete", grant.AllowDelete ? 1 : 0);
        command.Parameters.AddWithValue("$allowedExecutables", SerializeStrings(grant.AllowedExecutables));
        command.Parameters.AddWithValue(
            "$allowedEnvironmentVariables",
            SerializeStrings(grant.AllowedEnvironmentVariables));
        command.Parameters.AddWithValue("$allowPowerShell", grant.AllowPowerShell ? 1 : 0);
        command.Parameters.AddWithValue("$networkPolicyJson", grant.NetworkPolicyJson);
        command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(grant.ExpiresAt));
        command.Parameters.AddWithValue("$allowDelegation", grant.AllowDelegation ? 1 : 0);
        command.Parameters.AddWithValue("$delegatedAgentIds", SerializeStrings(grant.DelegatedAgentIds));
        command.Parameters.AddWithValue("$delegationChain", SerializeStrings(grant.DelegationChain));
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(grant.CreatedAt));
        command.Parameters.AddWithValue(
            "$revokedAt",
            grant.RevokedAt is { } revokedAt ? FormatTimestamp(revokedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$revocationReason", DbValue(grant.RevocationReason));
        command.Parameters.AddWithValue(
            "$approvalRequestId",
            DbValue(grant.ApprovalRequestId));
    }

    private static ToolGrantState ReadGrant(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            GetNullableString(reader, 2),
            GetNullableString(reader, 3),
            reader.GetString(4),
            reader.GetString(5),
            DeserializeStrings(reader.GetString(6)),
            DeserializeStrings(reader.GetString(7)),
            reader.GetInt32(8),
            DeserializeStrings(reader.GetString(9)),
            DeserializeStrings(reader.GetString(10)),
            reader.GetInt64(11) != 0,
            reader.GetInt64(12) != 0,
            reader.GetInt64(13) != 0,
            DeserializeStrings(reader.GetString(14)),
            DeserializeStrings(reader.GetString(15)),
            reader.GetInt64(16) != 0,
            reader.GetString(17),
            ParseTimestamp(reader.GetString(18)),
            reader.GetInt64(19) != 0,
            DeserializeStrings(reader.GetString(20)),
            DeserializeStrings(reader.GetString(21)),
            ParseTimestamp(reader.GetString(22)),
            reader.IsDBNull(23) ? null : ParseTimestamp(reader.GetString(23)),
            GetNullableString(reader, 24),
            GetNullableString(reader, 25));

    private static void ValidateIntentIdentity(ToolIntentState intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.CallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.ArgumentsHash);
        if ((intent.WorkStepId is null) != (intent.PlanVersion is null))
        {
            throw new ArgumentException(
                "A work tool intent must include both work step id and plan version.");
        }

        if (intent.WorkStepId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(intent.WorkStepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(intent.PlanVersion);
        }
    }

    private static void ValidateGrant(ToolGrantState grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.GrantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.RootGrantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.NetworkPolicyJson);
        ArgumentNullException.ThrowIfNull(grant.AllowedToolIds);
        ArgumentNullException.ThrowIfNull(grant.AllowedCallIds);
        ArgumentNullException.ThrowIfNull(grant.AllowedReadRoots);
        ArgumentNullException.ThrowIfNull(grant.AllowedWriteRoots);
        ArgumentNullException.ThrowIfNull(grant.AllowedExecutables);
        ArgumentNullException.ThrowIfNull(grant.AllowedEnvironmentVariables);
        ArgumentNullException.ThrowIfNull(grant.DelegatedAgentIds);
        ArgumentNullException.ThrowIfNull(grant.DelegationChain);
        using var _ = JsonDocument.Parse(grant.NetworkPolicyJson);
    }

    private static void ValidateApproval(ToolApprovalState approval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ApprovalRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.CallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.Decision);
    }

    private static void ValidateAudit(ToolAuditRecord audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.AuditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.CallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.ToolCatalogVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.ArgumentsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.TargetSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.DiagnosticId);
        ArgumentNullException.ThrowIfNull(audit.DelegationChain);
    }

    private static bool GrantEquivalent(ToolGrantState first, ToolGrantState second) =>
        string.Equals(first.GrantId, second.GrantId, StringComparison.Ordinal)
        && string.Equals(first.RunId, second.RunId, StringComparison.Ordinal)
        && string.Equals(first.AgentId, second.AgentId, StringComparison.Ordinal)
        && string.Equals(first.ParentGrantId, second.ParentGrantId, StringComparison.Ordinal)
        && string.Equals(first.RootGrantId, second.RootGrantId, StringComparison.Ordinal)
        && string.Equals(first.WorkspaceRoot, second.WorkspaceRoot, StringComparison.Ordinal)
        && first.AllowedToolIds.SequenceEqual(second.AllowedToolIds, StringComparer.Ordinal)
        && first.AllowedCallIds.SequenceEqual(second.AllowedCallIds, StringComparer.Ordinal)
        && first.MaximumRisk == second.MaximumRisk
        && first.AllowedReadRoots.SequenceEqual(second.AllowedReadRoots, StringComparer.Ordinal)
        && first.AllowedWriteRoots.SequenceEqual(second.AllowedWriteRoots, StringComparer.Ordinal)
        && first.AllowOverwrite == second.AllowOverwrite
        && first.AllowMove == second.AllowMove
        && first.AllowDelete == second.AllowDelete
        && first.AllowedExecutables.SequenceEqual(second.AllowedExecutables, StringComparer.Ordinal)
        && first.AllowedEnvironmentVariables.SequenceEqual(
            second.AllowedEnvironmentVariables,
            StringComparer.Ordinal)
        && first.AllowPowerShell == second.AllowPowerShell
        && string.Equals(first.NetworkPolicyJson, second.NetworkPolicyJson, StringComparison.Ordinal)
        && first.ExpiresAt.Equals(second.ExpiresAt)
        && first.AllowDelegation == second.AllowDelegation
        && first.DelegatedAgentIds.SequenceEqual(second.DelegatedAgentIds, StringComparer.Ordinal)
        && first.DelegationChain.SequenceEqual(second.DelegationChain, StringComparer.Ordinal)
        && first.CreatedAt.Equals(second.CreatedAt)
        && Nullable.Equals(first.RevokedAt, second.RevokedAt)
        && string.Equals(first.RevocationReason, second.RevocationReason, StringComparison.Ordinal)
        && string.Equals(
            first.ApprovalRequestId,
            second.ApprovalRequestId,
            StringComparison.Ordinal);

    private static string SerializeStrings(IEnumerable<string> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string[] DeserializeStrings(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("A persisted tool string collection is not an array.");
        }

        var values = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.String || element.GetString() is not { } value)
            {
                throw new InvalidDataException(
                    "A persisted tool string collection contains a non-string value.");
            }

            values.Add(value);
        }

        return values.ToArray();
    }

    private static string FormatStatus(ToolIntentStatus status) => status switch
    {
        ToolIntentStatus.Pending => "pending",
        ToolIntentStatus.Sent => "sent",
        ToolIntentStatus.Succeeded => "succeeded",
        ToolIntentStatus.Failed => "failed",
        ToolIntentStatus.Cancelled => "cancelled",
        ToolIntentStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static ToolIntentStatus ParseStatus(string status) => status switch
    {
        "pending" => ToolIntentStatus.Pending,
        "sent" => ToolIntentStatus.Sent,
        "succeeded" => ToolIntentStatus.Succeeded,
        "failed" => ToolIntentStatus.Failed,
        "cancelled" => ToolIntentStatus.Cancelled,
        "unknown" => ToolIntentStatus.Unknown,
        _ => throw new InvalidDataException($"Unknown persisted tool intent status '{status}'.")
    };

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static object DbValue(string? value) => (object?)value ?? DBNull.Value;

    public void Dispose()
    {
        _gate.Dispose();
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
