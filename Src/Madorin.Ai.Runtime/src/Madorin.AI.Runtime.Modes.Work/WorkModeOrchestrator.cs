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
    private const string StepProjectionVersion = "work-step-v1";

    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;
    private readonly SqliteSessionRepository _sessionRepo;
    private readonly SqliteWorkRepository _workRepo;
    private readonly SqliteEventOutbox _outbox;
    private readonly AgentContextComposer? _agentContextComposer;
    private readonly IToolStateStore? _toolStateStore;
    private readonly ToolGateway? _toolGateway;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot;
    private readonly ToolConsentCoordinator? _toolConsentCoordinator;
    private readonly RuntimeLimits _runtimeLimits;

    public WorkModeOrchestrator(
        Func<string, IRuntimeProviderAdapter> providerFactory,
        ConversationStore store,
        SqliteSessionRepository sessionRepo,
        SqliteEventOutbox outbox,
        AgentContextComposer? agentContextComposer = null,
        IToolStateStore? toolStateStore = null,
        ToolGateway? toolGateway = null,
        ToolCatalogSnapshot? toolCatalogSnapshot = null,
        RuntimeLimits? runtimeLimits = null,
        ToolConsentCoordinator? toolConsentCoordinator = null,
        SqliteWorkRepository? workRepo = null,
        SqliteConnection? connection = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        ArgumentNullException.ThrowIfNull(store);
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _agentContextComposer = agentContextComposer;
        _toolStateStore = toolStateStore;
        _toolGateway = toolGateway;
        _toolCatalogSnapshot = toolCatalogSnapshot;
        _toolConsentCoordinator = toolConsentCoordinator;
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
            long earlySequence = 0;
            yield return await CreateAcceptedEnvelopeAsync(
                runId, sessionId, runtimeInstanceId, earlySequence++, ct).ConfigureAwait(false);
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

        long runSequence = 0;
        yield return await CreateAcceptedEnvelopeAsync(
            runId, sessionId, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);

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
            }
        }

        yield return await CreateInvocationCompletedEnvelopeAsync(
            runId, runtimeInstanceId, managerInvocation.InvocationId, runSequence++, ct)
            .ConfigureAwait(false);

        var plan = TryParsePlan(managerText.ToString())
            ?? WorkPlanValidator.CreateFallbackPlan(
                goal, options, planVersion: "1", stepId: fallbackStepId);
        plan = WorkPlanValidator.ValidateOrThrow(plan, options);

        await _workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            options.GeneralManager.AgentId,
            policy,
            options.EffectiveContextPolicy,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            ct).ConfigureAwait(false);
        await _workRepo.SavePlanStepsAsync(sessionId, runId, plan, ct).ConfigureAwait(false);

        var agentById = options.AvailableAgents.ToDictionary(
            static a => a.AgentId, StringComparer.Ordinal);
        var completedCount = 0;

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
                    parentInvocationId: managerInvocation.InvocationId,
                    workStepId: readyStep.StepId,
                    workPlanVersion: readyStep.PlanVersion).ConfigureAwait(false);

                if (completedResult is null)
                {
                    var started = await _sessionRepo.TryStartWorkStepAsync(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        runId,
                        sessionId,
                        readyStep.TargetAgentId,
                        stepInputHash,
                        childInvocation.InvocationId,
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

                yield return await CreateInvocationStartedEnvelopeAsync(
                    childInvocation, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);

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
                            await _sessionRepo.FailWorkStepAsync(
                                readyStep.StepId, readyStep.PlanVersion, failed.Error.Message, ct)
                                .ConfigureAwait(false);
                            yield return await CreateInvocationFailedEnvelopeAsync(
                                runId,
                                runtimeInstanceId,
                                failed.InvocationId,
                                failed.Error,
                                runSequence++,
                                ct).ConfigureAwait(false);
                            if (policy.FailurePolicy is WorkStepFailurePolicy.Stop
                                or WorkStepFailurePolicy.AskHost)
                            {
                                await _workRepo.UpdateSessionStatusAsync(
                                    sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                                throw new InvalidOperationException(
                                    $"Work-step invocation '{failed.InvocationId}' failed: {failed.Error.Message}");
                            }

                            break;
                    }

                    if (stepFailed)
                    {
                        break;
                    }
                }

                if (stepFailed)
                {
                    if (policy.FailurePolicy is WorkStepFailurePolicy.Retry)
                    {
                        if (readyStep.AttemptCount < policy.MaxRetriesPerStep)
                        {
                            await _workRepo.ResetStepForRetryAsync(
                                readyStep.StepId,
                                readyStep.PlanVersion,
                                stepError?.Message ?? "Work step failed.",
                                ct).ConfigureAwait(false);
                            if (policy.RetryBackoffMilliseconds > 0)
                            {
                                await Task.Delay(policy.RetryBackoffMilliseconds, ct)
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

            var assistantContent = new List<ContentBlock>();
            RuntimeError? failure = null;
            var completed = false;
            await foreach (var providerEvent in provider.CompleteStreamingAsync(providerRequest, ct)
                .ConfigureAwait(false))
            {
                switch (providerEvent)
                {
                    case TextDeltaProviderEvent textDelta:
                        AccumulateAssistantContent(assistantContent, textDelta);
                        yield return textDelta;
                        break;
                    case ReasoningDeltaProviderEvent reasoningDelta:
                        AccumulateAssistantContent(assistantContent, reasoningDelta);
                        break;
                    case ToolCallCompleteProviderEvent toolCall:
                        AccumulateAssistantContent(assistantContent, toolCall);
                        yield return toolCall;
                        break;
                    case InvocationCompletedProviderEvent:
                        completed = true;
                        break;
                    case InvocationFailedProviderEvent failed:
                        failure = failed.Error;
                        yield return failed;
                        break;
                }

                if (failure is not null)
                {
                    yield break;
                }
            }

            if (failure is not null)
            {
                yield break;
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
                    ParentAgentId: null,
                    toolCall.Arguments.GetRawText(),
                    request.RunId,
                    request.SessionId,
                    request.InvocationId,
                    ToolCatalogVersion: _toolCatalogSnapshot.EffectiveVersion,
                    WorkStepId: request.Snapshot.WorkStepId,
                    PlanVersion: request.Snapshot.WorkPlanVersion);

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

                    gatewayResult = await _toolConsentCoordinator.ResolveAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        ct).ConfigureAwait(false);
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


    private async Task<AgentInvocationRequest> CreateInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        AgentRef agent,
        string projectionVersion,
        CancellationToken ct,
        string? stepGoal = null,
        string? parentInvocationId = null,
        string? workStepId = null,
        string? workPlanVersion = null)
    {
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var invocationId = Guid.NewGuid().ToString("N");
        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct).ConfigureAwait(false);
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
            workPlanVersion);
        ContentBlock[] userContent = stepGoal is null
            ? request.InitialInput
            : [new TextContentBlock(stepGoal)];
        return new AgentInvocationRequest(
            invocationId,
            runId,
            sessionId,
            agent.AgentId,
            providerId,
            modelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.System,
                    [new TextContentBlock(context.Instructions)]),
                new RuntimeProviderMessage(RuntimeProviderRoles.User, userContent),
            ],
            snapshot,
            AvailableTools: ResolveAvailableTools(agent, providerId));
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
