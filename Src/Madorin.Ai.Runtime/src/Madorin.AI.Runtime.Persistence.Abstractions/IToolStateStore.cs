namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Persists tool intents, grants, approvals, and redacted audit metadata.</summary>
public interface IToolStateStore
{
    public Task<bool> TryCreateIntentAsync(
        ToolIntentState intent,
        CancellationToken ct = default);

    public Task<ToolIntentState?> GetIntentAsync(
        string callId,
        CancellationToken ct = default);

    public Task<ToolIntentState?> GetCompletedWorkStepIntentAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        string toolId,
        string argumentsHash,
        CancellationToken ct = default);

    public Task<IReadOnlyList<ToolIntentState>> ListRecoverableIntentsAsync(
        CancellationToken ct = default);

    public Task<IReadOnlyList<ToolIntentState>> ListRecoverableWorkStepIntentsAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        CancellationToken ct = default);

    public Task<bool> TryMarkSentAsync(
        string callId,
        string grantId,
        string? approvalRequestId,
        DateTimeOffset sentAt,
        CancellationToken ct = default);

    public Task<bool> TryCompleteIntentAsync(
        ToolIntentCompletion completion,
        CancellationToken ct = default);

    public Task UpsertGrantAsync(
        ToolGrantState grant,
        CancellationToken ct = default);

    public Task<ToolGrantState?> GetGrantAsync(
        string grantId,
        CancellationToken ct = default);

    public Task<IReadOnlyList<ToolGrantState>> ListActiveGrantsAsync(
        string runId,
        DateTimeOffset now,
        CancellationToken ct = default);

    public Task<int> RevokeGrantAsync(
        string grantId,
        bool revokeDescendants,
        DateTimeOffset revokedAt,
        string? reason,
        CancellationToken ct = default);

    public Task SaveApprovalAsync(
        ToolApprovalState approval,
        CancellationToken ct = default);

    public Task<ToolApprovalState?> GetApprovalAsync(
        string approvalRequestId,
        CancellationToken ct = default);

    public Task AppendAuditAsync(
        ToolAuditRecord audit,
        CancellationToken ct = default);

    public Task CompleteAuditAsync(
        string auditId,
        string status,
        string? resultHash,
        DateTimeOffset completedAt,
        CancellationToken ct = default);
}
