using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services;

/// <summary>Coordinates startup recovery and Session resume across Runtime modes.</summary>
public sealed class RecoveryService
{
    private readonly ISessionRepository _repo;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot;
    private readonly ToolGateway? _toolGateway;
    private readonly IToolStateStore? _toolStateStore;
    private readonly IWorkRecoveryStore? _workRecoveryStore;

    public RecoveryService(
        ISessionRepository repo,
        IToolStateStore? toolStateStore = null,
        ToolGateway? toolGateway = null,
        ToolCatalogSnapshot? toolCatalogSnapshot = null,
        IWorkRecoveryStore? workRecoveryStore = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _toolStateStore = toolStateStore;
        _toolGateway = toolGateway;
        _toolCatalogSnapshot = toolCatalogSnapshot;
        _workRecoveryStore = workRecoveryStore;
    }

    /// <summary>Marks every non-terminal Run and active work step as interrupted.</summary>
    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        await RecoverSentToolIntentsAsync(ct).ConfigureAwait(false);
        await _repo.MarkInterruptedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queries sent tool calls and records manual intervention for unsafe unknown results.</summary>
    public async Task RecoverSentToolIntentsAsync(CancellationToken ct = default)
    {
        if (_toolStateStore is null
            || _toolGateway is null
            || _toolCatalogSnapshot is null)
        {
            return;
        }

        var intents = await _toolStateStore.ListRecoverableIntentsAsync(ct)
            .ConfigureAwait(false);
        foreach (var intent in intents)
        {
            ct.ThrowIfCancellationRequested();
            var current = intent;
            if (intent.Status is ToolIntentStatus.Sent)
            {
                _ = await _toolGateway.RecoverSentIntentAsync(
                    intent,
                    _toolCatalogSnapshot,
                    ct).ConfigureAwait(false);
                current = await _toolStateStore.GetIntentAsync(intent.CallId, ct)
                    .ConfigureAwait(false)
                    ?? intent;
            }

            if (current.Status is ToolIntentStatus.Unknown)
            {
                await MarkRequiresManualInterventionAsync(current, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Loads the persisted Session mode and latest Run state.</summary>
    public async Task<SessionResumeInfo?> ResumeSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = await _repo.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        return session?.LatestRun is not { } latestRun
            ? null
            : new SessionResumeInfo(
                sessionId,
                session.Mode,
                latestRun.RunId,
                latestRun.Status.ToString());
    }

    private async Task MarkRequiresManualInterventionAsync(
        ToolIntentState intent,
        CancellationToken ct)
    {
        if (_workRecoveryStore is null
            || string.IsNullOrWhiteSpace(intent.WorkStepId)
            || string.IsNullOrWhiteSpace(intent.PlanVersion))
        {
            return;
        }

        var reason = intent.ErrorMessage
            ?? $"Tool call '{intent.CallId}' has an unknown result and requires manual intervention.";
        await _workRecoveryStore.MarkRequiresManualInterventionAsync(
            intent.SessionId,
            intent.WorkStepId,
            intent.PlanVersion,
            reason,
            ct).ConfigureAwait(false);
    }
}
