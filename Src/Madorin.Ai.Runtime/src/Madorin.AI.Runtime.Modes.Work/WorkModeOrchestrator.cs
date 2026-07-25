using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Runs work-mode planning, validated step materialization, and sequential step execution.</summary>
public sealed class WorkModeOrchestrator : IModeOrchestrator
{
    private const string PlanProjectionVersion = "work-plan-v1";
    private const string StepProjectionVersion = "work-step-v2";

    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;
    private readonly ConversationStore _store;
    private readonly SqliteSessionRepository _sessionRepo;
    private readonly SqliteWorkRepository _workRepo;
    private readonly SqliteEventOutbox _outbox;
    private readonly AgentContextComposer? _agentContextComposer;
    private readonly bool _emitAccepted;
    private readonly long _startRunSequence;
    private readonly IToolStateStore? _toolStateStore;
    private readonly ToolGateway? _toolGateway;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot;
    private readonly ToolConsentCoordinator? _toolConsentCoordinator;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _workflowApprovalHandler;
    private readonly Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        _credentialRefreshHandler;
    private readonly RuntimeLimits _runtimeLimits;

    public WorkModeOrchestrator(
        Func<string, IRuntimeProviderAdapter> providerFactory,
        ConversationStore store,
        SqliteSessionRepository sessionRepo,
        SqliteEventOutbox outbox,
        AgentContextComposer? agentContextComposer = null,
        bool emitAccepted = true,
        long startRunSequence = 0,
        IToolStateStore? toolStateStore = null,
        ToolGateway? toolGateway = null,
        ToolCatalogSnapshot? toolCatalogSnapshot = null,
        RuntimeLimits? runtimeLimits = null,
        ToolConsentCoordinator? toolConsentCoordinator = null,
        SqliteWorkRepository? workRepo = null,
        SqliteConnection? connection = null,
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
            workflowApprovalHandler = null,
        Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
            credentialRefreshHandler = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _agentContextComposer = agentContextComposer;
        _emitAccepted = emitAccepted;
        _startRunSequence = startRunSequence;
        _toolStateStore = toolStateStore;
        _toolGateway = toolGateway;
        _toolCatalogSnapshot = toolCatalogSnapshot;
        _toolConsentCoordinator = toolConsentCoordinator;
        _workflowApprovalHandler = workflowApprovalHandler
            ?? (toolConsentCoordinator is { CanRequestApproval: true }
                ? toolConsentCoordinator.RequestWorkflowApprovalAsync
                : null);
        _credentialRefreshHandler = credentialRefreshHandler;
        _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
            MaxToolRounds: 8,
            MaxToolCallsPerRound: 8,
            MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);
        if (workRepo is not null)
        {
            _workRepo = workRepo;
        }
        else if (connection is not null)
        {
            _workRepo = new SqliteWorkRepository(connection);
        }
        else
        {
            // Best-effort: session repository owns the same connection for V1 wiring.
            _workRepo = new SqliteWorkRepository(sessionRepo.Connection);
        }
    }

    public RuntimeMode Mode => RuntimeMode.Work;

    public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        string runtimeInstanceId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ValidateWorkRequest(request);

        var options = (WorkModeOptions)request.Selection.ModeOptions;
        var policy = options.EffectiveWorkflowPolicy;
        policy.Validate();
        var goal = ExtractGoalText(request.InitialInput);
        var fallbackStepId = $"{runId}:step-1";
        var legacyInputHash = ComputeLegacyStepInputHash(request.InitialInput);
        var completedEarly = await _sessionRepo.GetCompletedWorkStepResultAsync(
            fallbackStepId,
            "1",
            legacyInputHash,
            ct).ConfigureAwait(false);
        if (completedEarly is not null)
        {
            long earlySequence = _startRunSequence;
            if (_emitAccepted)
            {
                yield return await CreateAcceptedEnvelopeAsync(
                    runId, sessionId, runtimeInstanceId, earlySequence++, ct).ConfigureAwait(false);
            }

            var replayAgent = options.AvailableAgents[0];
            var replayInvocation = await CreateInvocationRequestAsync(
                runId, sessionId, request, replayAgent, StepProjectionVersion, ct,
                stepGoal: goal).ConfigureAwait(false);
            yield return await CreateInvocationStartedEnvelopeAsync(
                replayInvocation, runtimeInstanceId, earlySequence++, ct).ConfigureAwait(false);
            var cached = JsonSerializer.Deserialize(
                completedEarly,
                RuntimeJsonContext.Default.TextContentBlock)
                ?? throw new InvalidDataException(
                    $"Work step '{fallbackStepId}' has an empty persisted result.");
            if (cached.Text.Length > 0)
            {
                yield return await CreateTextDeltaEnvelopeAsync(
                    runId, runtimeInstanceId, replayInvocation.InvocationId, cached.Text,
                    earlySequence++, ct).ConfigureAwait(false);
            }

            await EnsureWorkStepToolIntentsTerminalAsync(
                sessionId,
                fallbackStepId,
                "1",
                ct).ConfigureAwait(false);
            yield return await CreateInvocationCompletedEnvelopeAsync(
                runId, runtimeInstanceId, replayInvocation.InvocationId, earlySequence++, ct)
                .ConfigureAwait(false);
            await CompleteRunAsync(runId, ct).ConfigureAwait(false);
            yield return await CreateCompletedEnvelopeAsync(
                runId, sessionId, runtimeInstanceId, earlySequence, ct).ConfigureAwait(false);
            yield break;
        }

        await _workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Planning,
            options.GeneralManager.AgentId,
            policy,
            options.EffectiveContextPolicy,
            planVersion: "1",
            planMessageId: null,
            plan: null,
            ct).ConfigureAwait(false);

        long runSequence = _startRunSequence;
        if (_emitAccepted)
        {
            yield return await CreateAcceptedEnvelopeAsync(
                runId, sessionId, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
        }

        var managerInvocation = await CreateInvocationRequestAsync(
            runId, sessionId, request, options.GeneralManager, PlanProjectionVersion, ct)
            .ConfigureAwait(false);
        yield return await CreateInvocationStartedEnvelopeAsync(
            managerInvocation, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);

        var managerText = new StringBuilder();
        await foreach (var providerEvent in ExecuteInvocationAsync(managerInvocation, ct)
            .ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case TextDeltaProviderEvent textDelta:
                    managerText.Append(textDelta.Delta);
                    yield return await CreateTextDeltaEnvelopeAsync(
                        runId, runtimeInstanceId, textDelta.InvocationId, textDelta.Delta,
                        runSequence++, ct).ConfigureAwait(false);
                    break;
                case InvocationFailedProviderEvent failed:
                    await _workRepo.UpdateSessionStatusAsync(
                        sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"General-manager invocation '{failed.InvocationId}' failed: {failed.Error.Message}");
                case WorkRunStatusProviderEvent statusChanged:
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        statusChanged.Status,
                        ct).ConfigureAwait(false);
                    break;
            }
        }

        yield return await CreateInvocationCompletedEnvelopeAsync(
            runId, runtimeInstanceId, managerInvocation.InvocationId, runSequence++, ct)
            .ConfigureAwait(false);

        var plan = TryParsePlan(managerText.ToString())
            ?? WorkPlanValidator.CreateFallbackPlan(
                goal, options, planVersion: "1", stepId: fallbackStepId);
        plan = WorkPlanValidator.ValidateOrThrow(plan, options);
        var canonicalMessages = await AppendPlanRevisionAsync(
            sessionId,
            managerInvocation.InvocationId,
            options.GeneralManager.AgentId,
            plan,
            ct).ConfigureAwait(false);
        await _workRepo.SavePlanRevisionAsync(
            sessionId,
            runId,
            options.GeneralManager.AgentId,
            policy,
            options.EffectiveContextPolicy,
            plan,
            canonicalMessages.PlanMessageId,
            canonicalMessages.StepMessageIds,
            ct).ConfigureAwait(false);

        var agentById = options.AvailableAgents.ToDictionary(
            static a => a.AgentId, StringComparer.Ordinal);
        var completedCount = 0;
        var approvedLoopRisks = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var steps = await _workRepo.ListStepsAsync(sessionId, plan.PlanVersion, ct)
                .ConfigureAwait(false);
            if (steps.All(static s => s.Status is WorkStepLifecycleStatus.Completed
                    or WorkStepLifecycleStatus.Skipped
                    or WorkStepLifecycleStatus.Failed))
            {
                break;
            }

            var ready = WorkStepScheduler.SelectReadySteps(steps, policy, currentlyRunningCount: 0);
            if (ready.Count == 0)
            {
                if (steps.Any(static s => s.Status == WorkStepLifecycleStatus.Running))
                {
                    throw new InvalidOperationException(
                        "Work scheduler found running steps but no ready continuation.");
                }

                var failed = steps.FirstOrDefault(static s =>
                    s.Status == WorkStepLifecycleStatus.Failed);
                if (failed is not null)
                {
                    await _workRepo.UpdateSessionStatusAsync(
                        sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Work step '{failed.StepId}' failed: {failed.ErrorMessage}");
                }

                throw new InvalidOperationException(
                    "Work plan has pending steps but no ready steps (possible dependency deadlock).");
            }

            // V1 sequential execution within the selected ready set.
            foreach (var readyStep in ready)
            {
                if (!agentById.TryGetValue(readyStep.TargetAgentId, out var agent))
                {
                    throw new InvalidOperationException(
                        $"Step '{readyStep.StepId}' targets unknown agent '{readyStep.TargetAgentId}'.");
                }

                var stepInputHash = string.Equals(
                        readyStep.StepId,
                        fallbackStepId,
                        StringComparison.Ordinal)
                    ? legacyInputHash
                    : (string.IsNullOrWhiteSpace(readyStep.StepInputHash)
                        ? WorkPlanValidator.ComputeStepInputHash(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            readyStep.Goal ?? string.Empty)
                        : readyStep.StepInputHash);

                var completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                    readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                    .ConfigureAwait(false);

                var childInvocation = await CreateInvocationRequestAsync(
                    runId, sessionId, request, agent, StepProjectionVersion, ct,
                    stepGoal: readyStep.Goal ?? goal,
                    parentAgentId: options.GeneralManager.AgentId,
                    parentInvocationId: managerInvocation.InvocationId,
                    workStepId: readyStep.StepId,
                    workPlanVersion: readyStep.PlanVersion,
                    workPlan: plan,
                    currentStepState: readyStep,
                    planSteps: steps).ConfigureAwait(false);

                if (completedResult is null)
                {
                    var childSnapshotJson = JsonSerializer.Serialize(
                        childInvocation.Snapshot,
                        RuntimeJsonContext.Default.InvocationSnapshot);
                    var checkpointJson = CreateStepCheckpointJson(
                        readyStep,
                        childInvocation.InvocationId,
                        stepInputHash);
                    var started = await _sessionRepo.TryStartWorkStepWithCheckpointAsync(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        runId,
                        sessionId,
                        readyStep.TargetAgentId,
                        stepInputHash,
                        childInvocation.InvocationId,
                        childInvocation.Snapshot,
                        childSnapshotJson,
                        checkpointJson,
                        ct).ConfigureAwait(false);
                    if (!started)
                    {
                        completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                            readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                            .ConfigureAwait(false);
                        if (completedResult is null)
                        {
                            throw new InvalidOperationException(
                                $"Work step '{readyStep.StepId}' already exists without a reusable result.");
                        }
                    }
                }

                var loopRisk = DetectLoopRisk(readyStep, steps, policy);
                if (completedResult is null
                    && loopRisk is not null
                    && approvedLoopRisks.Add(loopRisk.ApprovalKey))
                {
                    var approved = await RequestWorkflowApprovalAsync(
                        sessionId,
                        runId,
                        readyStep,
                        childInvocation,
                        loopRisk.Reason,
                        ct).ConfigureAwait(false);
                    if (!approved)
                    {
                        await _sessionRepo.FailWorkStepAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            loopRisk.Reason,
                            ct).ConfigureAwait(false);
                        await _workRepo.UpdateSessionStatusAsync(
                            sessionId,
                            WorkSessionStatus.Failed,
                            ct).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"The host denied continuation for work step '{readyStep.StepId}': {loopRisk.Reason}");
                    }
                }

                yield return await CreateInvocationStartedEnvelopeAsync(
                    childInvocation, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
                if (IsProjectionAdjusted(childInvocation.Snapshot.ContextProjection))
                {
                    yield return await CreateContextProjectionAdjustedEnvelopeAsync(
                        childInvocation,
                        runtimeInstanceId,
                        runSequence++,
                        ct).ConfigureAwait(false);
                }

                if (completedResult is not null)
                {
                    var cached = JsonSerializer.Deserialize(
                        completedResult,
                        RuntimeJsonContext.Default.TextContentBlock)
                        ?? throw new InvalidDataException(
                            $"Work step '{readyStep.StepId}' has an empty persisted result.");
                    if (cached.Text.Length > 0)
                    {
                        yield return await CreateTextDeltaEnvelopeAsync(
                            runId, runtimeInstanceId, childInvocation.InvocationId, cached.Text,
                            runSequence++, ct).ConfigureAwait(false);
                    }

                    await EnsureWorkStepToolIntentsTerminalAsync(
                        sessionId,
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        ct).ConfigureAwait(false);
                    yield return await CreateInvocationCompletedEnvelopeAsync(
                        runId, runtimeInstanceId, childInvocation.InvocationId, runSequence++, ct)
                        .ConfigureAwait(false);
                    completedCount++;
                    continue;
                }

                var resultBuilder = new StringBuilder();
                var stepFailed = false;
                RuntimeError? stepError = null;
                await foreach (var providerEvent in ExecuteInvocationAsync(childInvocation, ct)
                    .ConfigureAwait(false))
                {
                    switch (providerEvent)
                    {
                        case TextDeltaProviderEvent textDelta:
                            resultBuilder.Append(textDelta.Delta);
                            yield return await CreateTextDeltaEnvelopeAsync(
                                runId, runtimeInstanceId, textDelta.InvocationId, textDelta.Delta,
                                runSequence++, ct).ConfigureAwait(false);
                            break;
                        case InvocationFailedProviderEvent failed:
                            stepFailed = true;
                            stepError = failed.Error;
                            if (policy.FailurePolicy is not WorkStepFailurePolicy.AskHost)
                            {
                                await _sessionRepo.FailWorkStepAsync(
                                    readyStep.StepId, readyStep.PlanVersion, failed.Error.Message, ct)
                                    .ConfigureAwait(false);
                            }
                            yield return await CreateInvocationFailedEnvelopeAsync(
                                runId,
                                runtimeInstanceId,
                                failed.InvocationId,
                                failed.Error,
                                runSequence++,
                                ct).ConfigureAwait(false);
                            if (policy.FailurePolicy is WorkStepFailurePolicy.Stop)
                            {
                                await _workRepo.UpdateSessionStatusAsync(
                                    sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                                throw new InvalidOperationException(
                                    $"Work-step invocation '{failed.InvocationId}' failed: {failed.Error.Message}");
                            }

                            break;
                        case WorkRunStatusProviderEvent statusChanged:
                            yield return await CreateStatusEnvelopeAsync(
                                runtimeInstanceId,
                                runId,
                                runSequence++,
                                statusChanged.Status,
                                ct).ConfigureAwait(false);
                            break;
                    }

                    if (stepFailed)
                    {
                        break;
                    }
                }

                if (stepFailed)
                {
                    if (policy.FailurePolicy is WorkStepFailurePolicy.AskHost)
                    {
                        var reason = stepError?.Message ?? "Work step failed.";
                        var approved = await RequestWorkflowApprovalAsync(
                            sessionId,
                            runId,
                            readyStep,
                            childInvocation,
                            reason,
                            ct).ConfigureAwait(false);
                        await _sessionRepo.FailWorkStepAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            reason,
                            ct).ConfigureAwait(false);
                        if (!approved)
                        {
                            await _workRepo.UpdateSessionStatusAsync(
                                sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                            throw new InvalidOperationException(
                                $"The host stopped work step '{readyStep.StepId}' after failure: {reason}");
                        }

                        await _workRepo.ResetStepForRetryAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            reason,
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    if (policy.FailurePolicy is WorkStepFailurePolicy.Retry)
                    {
                        if (readyStep.AttemptCount < policy.MaxRetriesPerStep)
                        {
                            await _workRepo.ResetStepForRetryAsync(
                                readyStep.StepId,
                                readyStep.PlanVersion,
                                stepError?.Message ?? "Work step failed.",
                                ct).ConfigureAwait(false);
                            var retryDelay = ComputeRetryDelay(policy, readyStep.AttemptCount);
                            if (retryDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(retryDelay, ct)
                                    .ConfigureAwait(false);
                            }

                            continue;
                        }

                        await _workRepo.UpdateSessionStatusAsync(
                            sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Work-step invocation '{childInvocation.InvocationId}' failed after {policy.MaxRetriesPerStep} retries: {stepError?.Message}");
                    }

                    completedCount++;
                    continue;
                }

                await EnsureWorkStepToolIntentsTerminalAsync(
                    sessionId,
                    readyStep.StepId,
                    readyStep.PlanVersion,
                    ct).ConfigureAwait(false);
                yield return await CreateInvocationCompletedEnvelopeAsync(
                    runId, runtimeInstanceId, childInvocation.InvocationId, runSequence++, ct)
                    .ConfigureAwait(false);

                var resultJson = JsonSerializer.Serialize(
                    new TextContentBlock(resultBuilder.ToString()),
                    RuntimeJsonContext.Default.TextContentBlock);
                await _sessionRepo.CompleteWorkStepAsync(
                    readyStep.StepId,
                    readyStep.PlanVersion,
                    stepInputHash,
                    resultJson,
                    ct).ConfigureAwait(false);
                completedCount++;
            }

            if (completedCount > policy.HardMaxStepsPerRun)
            {
                throw new InvalidOperationException("Work run exceeded hard step limit.");
            }
        }

        await _workRepo.UpdateSessionStatusAsync(
            sessionId, WorkSessionStatus.Completed, ct).ConfigureAwait(false);
        await CompleteRunAsync(runId, ct).ConfigureAwait(false);
        yield return await CreateCompletedEnvelopeAsync(
            runId, sessionId, runtimeInstanceId, runSequence, ct).ConfigureAwait(false);
    }

    private async Task CompleteRunAsync(string runId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await _sessionRepo.GetRunStatusAsync(runId, ct).ConfigureAwait(false);
            switch (current)
            {
                case RunStatus.Completed:
                    return;
                case RunStatus.Failed or RunStatus.Cancelled or RunStatus.Interrupted:
                    throw new InvalidOperationException(
                        $"Work run '{runId}' cannot complete because it is already {current}.");
                case RunStatus.Running:
                    try
                    {
                        await _sessionRepo.TransitionRunStatusAsync(
                            runId,
                            RunStatus.Running,
                            RunStatus.Persisting,
                            ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    break;
                case RunStatus.Persisting:
                    try
                    {
                        await _sessionRepo.TransitionRunToTerminalAsync(
                            runId,
                            RunStatus.Persisting,
                            RunStatus.Completed,
                            terminalText: null,
                            ct).ConfigureAwait(false);
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Work run '{runId}' cannot complete from {current}.");
            }
        }

        throw new InvalidOperationException($"Work run '{runId}' could not be marked Completed.");
    }

    private async Task EnsureWorkStepToolIntentsTerminalAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        CancellationToken ct)
    {
        if (_toolStateStore is null)
        {
            return;
        }

        var recoverable = await _toolStateStore.ListRecoverableWorkStepIntentsAsync(
            sessionId,
            workStepId,
            planVersion,
            ct).ConfigureAwait(false);
        if (recoverable.Count == 0)
        {
            return;
        }

        var sample = string.Join(
            ", ",
            recoverable
                .Take(5)
                .Select(static intent => $"{intent.CallId}:{intent.Status}"));
        throw new InvalidOperationException(
            $"Work step '{workStepId}' cannot be completed because {recoverable.Count} tool intent(s) are not terminal: {sample}.");
    }

    private static WorkPlanDraft? TryParsePlan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidate = text.Trim();
        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        candidate = candidate[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize(candidate, RuntimeJsonContext.Default.WorkPlanDraft);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractGoalText(ContentBlock[] input)
    {
        var sb = new StringBuilder();
        foreach (var block in input)
        {
            if (block is TextContentBlock text && text.Text.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(text.Text);
            }
        }

        return sb.Length == 0 ? "Complete the assigned work." : sb.ToString();
    }

    private async Task<(string PlanMessageId, IReadOnlyDictionary<string, string> StepMessageIds)>
        AppendPlanRevisionAsync(
            string sessionId,
            string managerInvocationId,
            string managerAgentId,
            WorkPlanDraft plan,
            CancellationToken ct)
    {
        var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
        var planRecord = await AppendCanonicalWorkBodyAsync(
            sessionId,
            managerInvocationId,
            managerAgentId,
            planJson,
            kind: "work.plan",
            plan.PlanVersion,
            stepId: null,
            ct).ConfigureAwait(false);

        var stepMessageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            var stepJson = JsonSerializer.Serialize(
                step,
                RuntimeJsonContext.Default.WorkPlanStepDraft);
            var stepRecord = await AppendCanonicalWorkBodyAsync(
                sessionId,
                managerInvocationId,
                managerAgentId,
                stepJson,
                kind: "work.step",
                plan.PlanVersion,
                step.StepId,
                ct).ConfigureAwait(false);
            stepMessageIds.Add(step.StepId, stepRecord.MessageId);
        }

        return (planRecord.MessageId, stepMessageIds);
    }

    private async Task<ConversationRecordV1> AppendCanonicalWorkBodyAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string bodyJson,
        string kind,
        string planVersion,
        string? stepId,
        CancellationToken ct)
    {
        var content = JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(bodyJson) },
            RuntimeJsonContext.Default.ContentBlockArray);
        var metadata = CreateWorkMessageMetadata(kind, planVersion, stepId);
        return await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationMessageDraft(
                invocationId,
                agentId,
                "assistant",
                content,
                DateTimeOffset.UtcNow,
                SummaryMetadata: metadata),
            ct).ConfigureAwait(false);
    }

    private static JsonElement CreateWorkMessageMetadata(
        string kind,
        string planVersion,
        string? stepId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("planVersion", planVersion);
            if (stepId is not null)
            {
                writer.WriteString("stepId", stepId);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string CreateStepCheckpointJson(
        WorkStepSnapshot step,
        string invocationId,
        string stepInputHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "work.step.running");
            writer.WriteString("stepId", step.StepId);
            writer.WriteString("planVersion", step.PlanVersion);
            writer.WriteString("invocationId", invocationId);
            writer.WriteString("stepInputHash", stepInputHash);
            writer.WriteString("stepMessageId", step.StepMessageId);
            writer.WriteStartArray("dependsOn");
            foreach (var dependency in step.DependsOn ?? [])
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<bool> RequestWorkflowApprovalAsync(
        string sessionId,
        string runId,
        WorkStepSnapshot step,
        AgentInvocationRequest invocation,
        string reason,
        CancellationToken ct)
    {
        var approvalRequestId = CreateWorkflowApprovalRequestId(
            runId,
            step.StepId,
            step.PlanVersion,
            step.AttemptCount + 1,
            reason);
        var correlationId = Guid.NewGuid().ToString("N");
        var request = new ApprovalRequest(
            correlationId,
            approvalRequestId,
            $"work:{step.StepId}:{step.AttemptCount + 1}",
            runId,
            invocation.AgentId,
            invocation.ParentAgentId,
            "work.failure-policy",
            ComputeHash(reason),
            ToolRiskLevel.Write,
            $"work-step:{step.StepId}",
            reason,
            sessionId,
            invocation.InvocationId,
            invocation.Snapshot.ParentInvocationId,
            step.StepId,
            step.PlanVersion);
        var requestJson = JsonSerializer.Serialize(
            request,
            RuntimeJsonContext.Default.ApprovalRequest);
        await _workRepo.MarkStepWaitingForApprovalAsync(
            sessionId,
            step.StepId,
            step.PlanVersion,
            approvalRequestId,
            requestJson,
            ct).ConfigureAwait(false);

        if (_workflowApprovalHandler is null)
        {
            await _workRepo.MarkRequiresManualInterventionAsync(
                sessionId,
                step.StepId,
                step.PlanVersion,
                "Workflow approval is required but the host approval channel is unavailable.",
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Workflow approval is required but the host approval channel is unavailable.");
        }

        var response = await _workflowApprovalHandler(request, ct)
            .ConfigureAwait(false);
        if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
            || !string.Equals(response.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal))
        {
            await _workRepo.MarkRequiresManualInterventionAsync(
                sessionId,
                step.StepId,
                step.PlanVersion,
                "The host returned a workflow approval response for another request.",
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                "The host returned a workflow approval response for another request.");
        }

        await _workRepo.MarkStepRunningAfterApprovalAsync(
            sessionId,
            step.StepId,
            step.PlanVersion,
            approvalRequestId,
            ct).ConfigureAwait(false);
        return response.Decision is ToolAuthorizationDecision.Granted;
    }

    private static WorkLoopRisk? DetectLoopRisk(
        WorkStepSnapshot current,
        IReadOnlyList<WorkStepSnapshot> allSteps,
        WorkflowPolicy policy)
    {
        if (policy.LoopDetection is WorkLoopDetection.Disabled
            || !policy.RequireHumanConfirmationOnLoop)
        {
            return null;
        }

        if (current.Depth >= policy.MaxAgentDepth)
        {
            return new WorkLoopRisk(
                $"depth:{current.StepId}:{current.PlanVersion}",
                $"Step '{current.StepId}' reached the configured Agent depth threshold {policy.MaxAgentDepth}.");
        }

        var repeatedGoal = allSteps
            .Where(step => string.Equals(step.TargetAgentId, current.TargetAgentId, StringComparison.Ordinal)
                && string.Equals(
                    NormalizeGoal(step.Goal),
                    NormalizeGoal(current.Goal),
                    StringComparison.Ordinal))
            .OrderBy(static step => step.StepId, StringComparer.Ordinal)
            .ToArray();
        if (repeatedGoal.Length > 1
            && !string.Equals(repeatedGoal[0].StepId, current.StepId, StringComparison.Ordinal))
        {
            return new WorkLoopRisk(
                $"goal:{current.TargetAgentId}:{NormalizeGoal(current.Goal)}",
                $"Step '{current.StepId}' repeats the same target and normalized goal without a distinct objective.");
        }

        if (current.AttemptCount > Math.Max(1, policy.MaxRetriesPerStep))
        {
            return new WorkLoopRisk(
                $"progress:{current.StepId}:{current.PlanVersion}:{current.AttemptCount}",
                $"Step '{current.StepId}' is being attempted again without a completed output.");
        }

        return null;
    }

    private static TimeSpan ComputeRetryDelay(WorkflowPolicy policy, int completedAttempts)
    {
        if (policy.RetryBackoffMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Clamp(completedAttempts, 0, 6);
        var milliseconds = Math.Min(
            60_000L,
            checked((long)policy.RetryBackoffMilliseconds * (1L << exponent)));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string CreateWorkflowApprovalRequestId(
        string runId,
        string stepId,
        string planVersion,
        int attemptNumber,
        string reason)
    {
        var identity = $"{runId}\n{stepId}\n{planVersion}\n{attemptNumber}\n{reason}";
        return $"work-approval-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    private static string NormalizeGoal(string? goal) =>
        string.Join(
            ' ',
            (goal ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private sealed record WorkLoopRisk(string ApprovalKey, string Reason);

    private sealed record WorkRunStatusProviderEvent(
        string InvocationId,
        RunStatus Status) : RuntimeProviderEvent(InvocationId);

    private Task<RuntimeEventEnvelope> CreateAcceptedEnvelopeAsync(
        string runId, string sessionId, string runtimeInstanceId, long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.RunAccepted,
            new RunAcceptedEvent(sessionId, runId), RuntimeJsonContext.Default.RunAcceptedEvent, ct);

    private Task<RuntimeEventEnvelope> CreateCompletedEnvelopeAsync(
        string runId, string sessionId, string runtimeInstanceId, long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.RunCompleted,
            new RunCompletedEvent(runId, sessionId), RuntimeJsonContext.Default.RunCompletedEvent, ct);

    private Task<RuntimeEventEnvelope> CreateStatusEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus status,
        CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunStatusChanged,
            new RunStatusChangedEvent(runId, status.ToString()),
            RuntimeJsonContext.Default.RunStatusChangedEvent,
            ct);

    private async Task<RuntimeEventEnvelope> CreateInvocationStartedEnvelopeAsync(
        AgentInvocationRequest request,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct)
    {
        var snapshotJson = JsonSerializer.Serialize(
            request.Snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        await _sessionRepo.SaveInvocationSnapshotAsync(
            request.RunId,
            request.SessionId,
            request.Snapshot,
            snapshotJson,
            ct).ConfigureAwait(false);
        return await CreateEnvelopeAsync(
            runtimeInstanceId,
            request.RunId,
            runSequence,
            MessageTypes.InvocationStarted,
            new InvocationStartedEvent(request.InvocationId, request.Snapshot),
            RuntimeJsonContext.Default.InvocationStartedEvent,
            ct).ConfigureAwait(false);
    }

    private Task<RuntimeEventEnvelope> CreateInvocationCompletedEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.InvocationCompleted,
            new InvocationCompletedEvent(invocationId),
            RuntimeJsonContext.Default.InvocationCompletedEvent, ct);

    private Task<RuntimeEventEnvelope> CreateContextProjectionAdjustedEnvelopeAsync(
        AgentInvocationRequest request,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct)
    {
        var projection = request.Snapshot.ContextProjection
            ?? throw new InvalidOperationException(
                $"Invocation '{request.InvocationId}' has no context-projection snapshot.");
        return CreateEnvelopeAsync(
            runtimeInstanceId,
            request.RunId,
            runSequence,
            MessageTypes.ContextProjectionAdjusted,
            new ContextProjectionAdjustedEvent(
                request.InvocationId,
                projection.DroppedMessageCount,
                projection.IncludedMessageCount,
                projection.EstimatedTokens,
                projection.EstimateSource,
                projection.Strategy,
                projection.RetainedItems,
                projection.TokenLimit,
                projection.SummarizedUnitCount),
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent,
            ct);
    }

    private static bool IsProjectionAdjusted(ContextProjectionSnapshot? projection) =>
        projection is not null
        && (projection.DroppedMessageCount > 0
            || projection.SummarizedUnitCount > 0
            || !string.Equals(projection.Strategy, "Full", StringComparison.Ordinal));

    private Task<RuntimeEventEnvelope> CreateInvocationFailedEnvelopeAsync(
        string runId,
        string runtimeInstanceId,
        string invocationId,
        RuntimeError error,
        long runSequence,
        CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.InvocationFailed,
            new InvocationFailedEvent(invocationId, error),
            RuntimeJsonContext.Default.InvocationFailedEvent,
            ct);

    private Task<RuntimeEventEnvelope> CreateTextDeltaEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, string delta,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.TextDelta,
            new TextDeltaEvent(invocationId, delta), RuntimeJsonContext.Default.TextDeltaEvent, ct);

    private async Task<RuntimeEventEnvelope> CreateEnvelopeAsync<T>(
        string runtimeInstanceId, string runId, long runSequence, string messageType,
        T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : notnull
    {
        var payload = JsonSerializer.SerializeToElement(value, typeInfo);
        var gsn = await _outbox.AppendAsync(runId, runSequence, messageType, payload.GetRawText(), ct)
            .ConfigureAwait(false);
        return new RuntimeEventEnvelope(
            runtimeInstanceId, gsn, runId, runSequence, messageType, DateTimeOffset.UtcNow, payload);
    }

    private async IAsyncEnumerable<RuntimeProviderEvent> ExecuteInvocationAsync(
        AgentInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = _providerFactory(request.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{request.ProviderId}' was not found.");

        var messages = request.Messages.ToList();
        var tools = request.AvailableTools;
        var maxToolRounds = _runtimeLimits.MaxToolRounds ?? 8;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound ?? 8;
        var maxToolResultBytes = _runtimeLimits.MaxToolResultBytesPerInvocation
            ?? 4L * 1024 * 1024;
        long totalToolResultBytes = 0;
        var hasIrreversibleToolSideEffects = false;

        for (var toolRound = 0; ; toolRound++)
        {
            var providerRequest = new RuntimeProviderRequest(
                request.InvocationId,
                request.AgentId,
                request.ProviderId,
                request.ModelId,
                [.. messages],
                tools,
                IsIdempotent: !hasIrreversibleToolSideEffects,
                HasIrreversibleToolSideEffects: hasIrreversibleToolSideEffects,
                CancellationToken: ct);

            List<ContentBlock> assistantContent;
            RuntimeError? failure;
            var completed = false;
            for (var providerAttempt = 0; ; providerAttempt++)
            {
                assistantContent = [];
                failure = null;
                completed = false;
                var hasObservedProviderOutput = false;
                var attemptRequest = providerRequest.CreateAttempt(providerAttempt);
                await foreach (var providerEvent in provider.CompleteStreamingAsync(attemptRequest, ct)
                    .ConfigureAwait(false))
                {
                    switch (providerEvent)
                    {
                        case TextDeltaProviderEvent textDelta:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, textDelta);
                            yield return textDelta;
                            break;
                        case ReasoningDeltaProviderEvent reasoningDelta:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, reasoningDelta);
                            break;
                        case ToolCallDeltaProviderEvent:
                            hasObservedProviderOutput = true;
                            break;
                        case ToolCallCompleteProviderEvent toolCall:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, toolCall);
                            yield return toolCall;
                            break;
                        case UsageUpdatedProviderEvent:
                            hasObservedProviderOutput = true;
                            break;
                        case InvocationCompletedProviderEvent:
                            completed = true;
                            break;
                        case InvocationFailedProviderEvent failed:
                            failure = failed.Error;
                            break;
                    }

                    if (failure is not null)
                    {
                        break;
                    }
                }

                if (failure is null)
                {
                    break;
                }

                if (providerAttempt != 0
                    || !IsAuthenticationFailure(failure)
                    || !ProviderRetryPolicy.CanRetry(providerRequest, hasObservedProviderOutput)
                    || _credentialRefreshHandler is null)
                {
                    yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
                    yield break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    request.RunId,
                    RunStatus.Running,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);
                await _workRepo.MarkCredentialsWaitAsync(
                    request.SessionId,
                    request.Snapshot.WorkStepId,
                    request.Snapshot.WorkPlanVersion,
                    ct).ConfigureAwait(false);
                yield return new WorkRunStatusProviderEvent(
                    request.InvocationId,
                    RunStatus.WaitingForCredentials);

                bool refreshed;
                try
                {
                    refreshed = await _credentialRefreshHandler(
                            new CredentialsRefreshRequestedEvent(
                                request.RunId,
                                request.ProviderId,
                                "Unauthorized"),
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await _workRepo.MarkSessionInterruptedAsync(
                            request.SessionId,
                            "Provider credential refresh was interrupted.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                await _workRepo.MarkCredentialsWaitEndedAsync(
                        request.SessionId,
                        request.Snapshot.WorkStepId,
                        request.Snapshot.WorkPlanVersion,
                        ct)
                    .ConfigureAwait(false);
                await _sessionRepo.TransitionRunStatusAsync(
                    request.RunId,
                    RunStatus.WaitingForCredentials,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
                yield return new WorkRunStatusProviderEvent(
                    request.InvocationId,
                    RunStatus.Running);
                if (!refreshed)
                {
                    yield return new InvocationFailedProviderEvent(
                        request.InvocationId,
                        CreateRuntimeError(
                            "CredentialRefreshTimeout",
                            "Provider credentials were not refreshed before the waiting period expired."));
                    yield break;
                }
            }

            if (!completed)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ProviderProtocolError,
                        "The Provider stream ended before InvocationCompleted."));
                yield break;
            }

            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                yield break;
            }

            if (toolRound >= maxToolRounds)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        $"The Invocation exceeded the {maxToolRounds}-round tool limit."));
                yield break;
            }

            if (toolCalls.Length > maxToolCallsPerRound)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        $"The Provider requested {toolCalls.Length} tools in one round; the limit is {maxToolCallsPerRound}."));
                yield break;
            }

            if (toolCalls.Select(static call => call.CallId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                != toolCalls.Length)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ToolCallConflict,
                        "The Provider returned duplicate tool call ids in one round."));
                yield break;
            }

            if (_toolGateway is null || _toolCatalogSnapshot is null)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ToolExecutorUnavailable,
                        "The selected Runtime does not have a tool gateway."));
                yield break;
            }

            if (tools is not null)
            {
                var allowedToolIds = tools
                    .Select(static tool => tool.ToolId)
                    .ToHashSet(StringComparer.Ordinal);
                if (toolCalls.Any(call => !allowedToolIds.Contains(call.ToolId)))
                {
                    yield return new InvocationFailedProviderEvent(
                        request.InvocationId,
                        CreateRuntimeError(
                            RuntimeErrorCodes.ToolPermissionRequired,
                            "The Provider requested a tool outside the Work invocation's allowed tool set."));
                    yield break;
                }
            }

            messages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));

            foreach (var toolCall in toolCalls)
            {
                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    request.AgentId,
                    request.ParentAgentId,
                    toolCall.Arguments.GetRawText(),
                    request.RunId,
                    request.SessionId,
                    request.InvocationId,
                    ToolCatalogVersion: _toolCatalogSnapshot.EffectiveVersion,
                    WorkStepId: request.Snapshot.WorkStepId,
                    PlanVersion: request.Snapshot.WorkPlanVersion,
                    ParentInvocationId: request.Snapshot.ParentInvocationId);

                ToolGatewayResult gatewayResult;
                try
                {
                    gatewayResult = await _toolGateway.ExecuteAsync(
                        invocation,
                        permission: null,
                        _toolCatalogSnapshot,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsApproval
                    or ToolGatewayResultKind.NeedsPermission)
                {
                    if (_toolConsentCoordinator is null)
                    {
                        yield return new InvocationFailedProviderEvent(
                            request.InvocationId,
                            gatewayResult.Error ?? CreateRuntimeError(
                                RuntimeErrorCodes.ToolPermissionRequired,
                                "The Work tool call requires host consent."));
                        yield break;
                    }

                    var consentRequestId = ToolConsentCoordinator.CreateConsentRequestId(
                        gatewayResult.Kind,
                        invocation);
                    if (!string.IsNullOrWhiteSpace(invocation.WorkStepId)
                        && !string.IsNullOrWhiteSpace(invocation.PlanVersion))
                    {
                        await _workRepo.MarkStepWaitingForApprovalAsync(
                            invocation.SessionId,
                            invocation.WorkStepId,
                            invocation.PlanVersion,
                            consentRequestId,
                            CreatePendingConsentJson(gatewayResult.Kind, invocation, consentRequestId),
                            ct).ConfigureAwait(false);
                    }

                    gatewayResult = await _toolConsentCoordinator.ResolveAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(invocation.WorkStepId)
                        && !string.IsNullOrWhiteSpace(invocation.PlanVersion))
                    {
                        await _workRepo.MarkStepRunningAfterApprovalAsync(
                            invocation.SessionId,
                            invocation.WorkStepId,
                            invocation.PlanVersion,
                            consentRequestId,
                            ct).ConfigureAwait(false);
                    }
                }

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                totalToolResultBytes += GetContentByteCount(toolResult);
                if (HasOversizedInlineContent(toolResult, _runtimeLimits.MaxInlineContentBytes)
                    || totalToolResultBytes > maxToolResultBytes)
                {
                    yield return new InvocationFailedProviderEvent(
                        request.InvocationId,
                        CreateRuntimeError(
                            RuntimeErrorCodes.LimitsIncompatible,
                            "Tool results exceeded the configured inline or Invocation size limit."));
                    yield break;
                }

                messages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
                hasIrreversibleToolSideEffects = true;
            }
        }
    }

    private static void AccumulateAssistantContent(
        List<ContentBlock> content,
        RuntimeProviderEvent providerEvent)
    {
        switch (providerEvent)
        {
            case TextDeltaProviderEvent text:
                if (content.LastOrDefault() is TextContentBlock previousText)
                {
                    content[^1] = previousText with { Text = previousText.Text + text.Delta };
                }
                else
                {
                    content.Add(new TextContentBlock(text.Delta));
                }

                break;
            case ReasoningDeltaProviderEvent reasoning:
                if (content.LastOrDefault() is ReasoningContentBlock previousReasoning)
                {
                    content[^1] = previousReasoning with
                    {
                        Content = previousReasoning.Content + reasoning.Delta
                    };
                }
                else
                {
                    content.Add(new ReasoningContentBlock(reasoning.Delta, reasoning.ProviderExtensions));
                }

                break;
            case ToolCallCompleteProviderEvent toolCall:
                using (var arguments = JsonDocument.Parse(toolCall.ArgumentsJson))
                {
                    content.Add(new ToolCallContentBlock(
                        toolCall.CallId,
                        toolCall.ToolId,
                        toolCall.Name,
                        arguments.RootElement.Clone()));
                }

                break;
        }
    }

    private static string CreatePendingConsentJson(
        ToolGatewayResultKind kind,
        ToolInvocation invocation,
        string consentRequestId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind switch
            {
                ToolGatewayResultKind.NeedsApproval => "approval",
                ToolGatewayResultKind.NeedsPermission => "permission",
                _ => "unknown"
            });
            writer.WriteString("requestId", consentRequestId);
            writer.WriteString("callId", invocation.CallId);
            writer.WriteString("toolId", invocation.ToolId);
            writer.WriteString("agentId", invocation.AgentId);
            writer.WriteString("parentAgentId", invocation.ParentAgentId);
            writer.WriteString("runId", invocation.RunId);
            writer.WriteString("sessionId", invocation.SessionId);
            writer.WriteString("invocationId", invocation.InvocationId);
            writer.WriteString("parentInvocationId", invocation.ParentInvocationId);
            writer.WriteString("workStepId", invocation.WorkStepId);
            writer.WriteString("planVersion", invocation.PlanVersion);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ToolResultContentBlock CreateToolResultContent(
        ToolCallContentBlock toolCall,
        ToolGatewayResult gatewayResult)
    {
        var success = gatewayResult.Kind is ToolGatewayResultKind.Success
            && gatewayResult.Result is { Success: true };
        ContentBlock[] content;
        if (success)
        {
            var blocks = new List<ContentBlock>(2);
            if (gatewayResult.Result!.OutputJson is { } outputJson)
            {
                blocks.Add(new TextContentBlock(outputJson));
            }

            if (gatewayResult.Result.ResultBlob is { } resultBlob)
            {
                blocks.Add(new BlobRefContentBlock(resultBlob));
            }

            content = [.. blocks];
        }
        else
        {
            content =
            [
                new TextContentBlock(
                    gatewayResult.Error?.Message
                    ?? gatewayResult.Result?.Error
                    ?? "Tool execution failed.")
            ];
        }

        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            success,
            content);
    }

    private static long GetContentByteCount(ToolResultContentBlock content) =>
        content.Content.Aggregate(
            0L,
            static (total, block) => checked(
                total + (block switch
                {
                    TextContentBlock text => Encoding.UTF8.GetByteCount(text.Text),
                    BlobRefContentBlock blob => blob.Blob.Length,
                    _ => 0
                })));

    private static bool HasOversizedInlineContent(
        ToolResultContentBlock content,
        int maxInlineContentBytes) =>
        content.Content
            .OfType<TextContentBlock>()
            .Any(text => Encoding.UTF8.GetByteCount(text.Text) > maxInlineContentBytes);

    private static RuntimeError CreateRuntimeError(string code, string message) =>
        new(
            code,
            "orchestration",
            message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));

    private static bool IsAuthenticationFailure(RuntimeError error) =>
        string.Equals(error.Code, RuntimeErrorCodes.AuthenticationFailed, StringComparison.Ordinal)
        || string.Equals(error.Category, "authentication", StringComparison.Ordinal);


    private async Task<AgentInvocationRequest> CreateInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        AgentRef agent,
        string projectionVersion,
        CancellationToken ct,
        string? stepGoal = null,
        string? parentAgentId = null,
        string? parentInvocationId = null,
        string? workStepId = null,
        string? workPlanVersion = null,
        WorkPlanDraft? workPlan = null,
        WorkStepSnapshot? currentStepState = null,
        IReadOnlyList<WorkStepSnapshot>? planSteps = null)
    {
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var provider = _providerFactory(providerId)
            ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");
        var invocationId = Guid.NewGuid().ToString("N");
        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct).ConfigureAwait(false);
        var availableTools = ResolveAvailableTools(agent, providerId);
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(context.Instructions)]);
        RuntimeProviderMessage[] projectedMessages;
        ContextProjectionSnapshot? projectionSnapshot = null;
        if (string.Equals(projectionVersion, PlanProjectionVersion, StringComparison.Ordinal)
            && request.Selection.ModeOptions is WorkModeOptions workOptions)
        {
            projectedMessages =
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock(BuildManagerPlanningInput(
                        ExtractGoalText(request.InitialInput),
                        workOptions,
                        availableTools))])
            ];
        }
        else if (workPlan is not null
            && currentStepState is not null
            && planSteps is not null
            && request.Selection.ModeOptions is WorkModeOptions projectedWorkOptions)
        {
            var currentStep = workPlan.Steps.FirstOrDefault(step =>
                string.Equals(step.StepId, currentStepState.StepId, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Work plan '{workPlan.PlanVersion}' does not contain step '{currentStepState.StepId}'.");
            var modelContextWindow = provider.Models
                .FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
                ?.ContextWindow;
            var modelContextLimit = modelContextWindow is > 0
                ? (int)Math.Max(1, modelContextWindow.Value * 9L / 10L)
                : int.MaxValue;
            var configuredLimit = projectedWorkOptions.EffectiveContextPolicy.MaxTokens;
            var tokenLimit = configuredLimit is > 0
                ? Math.Min(configuredLimit.Value, modelContextLimit)
                : modelContextLimit;
            ValueTask<ProviderTokenEstimate> EstimateProjectionAsync(
                RuntimeProviderMessage[] candidateMessages,
                CancellationToken token) =>
                provider.EstimateTokensAsync(
                    new RuntimeProviderRequest(
                        invocationId,
                        agent.AgentId,
                        providerId,
                        modelId,
                        [systemMessage, .. candidateMessages],
                        Tools: availableTools,
                        CancellationToken: token),
                    token);
            var projection = await WorkContextProjectionBuilder.BuildAsync(
                    workPlan,
                    currentStep,
                    currentStepState,
                    planSteps,
                    projectedWorkOptions.EffectiveContextPolicy,
                    tokenLimit,
                    EstimateProjectionAsync,
                    ct: ct)
                .ConfigureAwait(false);
            projectedMessages = projection.Messages;
            projectionSnapshot = new ContextProjectionSnapshot(
                projection.Strategy,
                projection.RetainedItems,
                projection.DroppedMessageCount,
                projection.Messages.Length,
                projection.EstimatedTokens,
                projection.EstimateSource,
                projection.TokenLimit,
                projection.SummarizedUnitCount);
        }
        else
        {
            var userContent = stepGoal is null
                ? request.InitialInput
                : [new TextContentBlock(stepGoal)];
            projectedMessages =
            [
                new RuntimeProviderMessage(RuntimeProviderRoles.User, userContent)
            ];
        }

        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            projectionVersion,
            DateTimeOffset.UtcNow,
            parentInvocationId,
            workStepId,
            workPlanVersion,
            projectionSnapshot);

        return new AgentInvocationRequest(
            invocationId,
            runId,
            sessionId,
            agent.AgentId,
            providerId,
            modelId,
            [
                systemMessage,
                .. projectedMessages
            ],
            snapshot,
            AvailableTools: availableTools,
            ParentAgentId: parentAgentId);
    }

    private static string BuildManagerPlanningInput(
        string goal,
        WorkModeOptions options,
        ToolDescriptor[]? tools)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Create a structured WorkPlanDraft for this goal:");
        builder.AppendLine(goal);
        builder.AppendLine("Available agents and declared capabilities:");
        foreach (var agent in options.AvailableAgents)
        {
            builder.Append("- ").Append(agent.AgentId);
            if (agent.AllowedToolIds is { Length: > 0 })
            {
                builder.Append("; tools=").AppendJoin(',', agent.AllowedToolIds);
            }

            if (agent.SkillIds is { Length: > 0 })
            {
                builder.Append("; skills=").AppendJoin(',', agent.SkillIds);
            }

            builder.AppendLine();
        }

        builder.AppendLine("Runtime tool catalog visible to the general manager:");
        if (tools is null || tools.Length == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var tool in tools)
            {
                builder.Append("- ").Append(tool.ToolId).Append(':').AppendLine(tool.DisplayName);
            }
        }

        builder.Append("WorkflowPolicy: ").AppendLine(
            JsonSerializer.Serialize(
                options.EffectiveWorkflowPolicy,
                RuntimeJsonContext.Default.WorkflowPolicy));
        builder.AppendLine(
            "Return only a WorkPlanDraft JSON object. The first planVersion is '1'.");
        return builder.ToString();
    }

    private ToolDescriptor[]? ResolveAvailableTools(AgentRef agent, string providerId)
    {
        if (_toolGateway is null || _toolCatalogSnapshot is null)
        {
            return null;
        }

        if (agent.AllowedToolIds is { Length: > 0 } allowedToolIds)
        {
            var requestedToolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolId in allowedToolIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
                if (!requestedToolIds.Add(toolId))
                {
                    throw new ArgumentException(
                        $"Work-mode Agent '{agent.AgentId}' declares duplicate tool ID '{toolId}'.",
                        nameof(agent));
                }

                if (!_toolCatalogSnapshot.TryGetTool(toolId, out _))
                {
                    throw new ArgumentException(
                        $"Work-mode Agent '{agent.AgentId}' declares unknown tool ID '{toolId}'.",
                        nameof(agent));
                }
            }

            var provider = _providerFactory(providerId)
                ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");
            if (!provider.Capabilities.ToolCalling)
            {
                throw new ArgumentException(
                    $"Provider '{providerId}' does not support tools required by Work-mode Agent '{agent.AgentId}'.",
                    nameof(agent));
            }

            return _toolCatalogSnapshot.Tools
                .Where(tool => requestedToolIds.Contains(tool.ToolId))
                .ToArray();
        }

        // Expert-compatible default: expose the full catalog when the Runtime has a gateway.
        return [.. _toolCatalogSnapshot.Tools];
    }


    private static void ValidateWorkRequest(NewSessionRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.InitialInput);
        if (request.Mode is not RuntimeMode.Work
            || request.Selection.Mode is not RuntimeMode.Work
            || request.Selection.ModeOptions is not WorkModeOptions options)
        {
            throw new ArgumentException("The request must contain Work mode options.", nameof(request));
        }

        options.EffectiveWorkflowPolicy.Validate();
        ValidateAgent(options.GeneralManager, nameof(options.GeneralManager));
        ArgumentNullException.ThrowIfNull(options.AvailableAgents);
        if (options.AvailableAgents.Length == 0)
        {
            throw new ArgumentException(
                "Work mode requires at least one available child Agent.", nameof(request));
        }

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in options.AvailableAgents)
        {
            ValidateAgent(agent, nameof(options.AvailableAgents));
            if (!agentIds.Add(agent.AgentId))
            {
                throw new ArgumentException(
                    $"Work-mode Agent ID '{agent.AgentId}' is duplicated.", nameof(request));
            }
        }
    }

    private static void ValidateAgent(AgentRef agent, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(agent, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.PromptTemplateVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.SystemPrompt);
    }

    private static string ComputeLegacyStepInputHash(ContentBlock[] input)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            input,
            RuntimeJsonContext.Default.ContentBlockArray);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();


}
