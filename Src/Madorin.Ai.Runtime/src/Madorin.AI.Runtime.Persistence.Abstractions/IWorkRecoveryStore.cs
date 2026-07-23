namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Persists recovery-only work-mode decisions.</summary>
public interface IWorkRecoveryStore
{
    public Task MarkRequiresManualInterventionAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        string reason,
        CancellationToken ct = default);
}
