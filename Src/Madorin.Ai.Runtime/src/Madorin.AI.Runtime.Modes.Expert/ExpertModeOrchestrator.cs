using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

public sealed class ExpertModeOrchestrator(
    IRuntimeProviderAdapter provider,
    ConversationStore store,
    SqliteSessionRepository sessionRepo,
    SqliteEventOutbox outbox,
    AgentContextComposer? agentContextComposer = null,
    Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        credentialRefreshHandler = null,
    ToolGateway? toolGateway = null,
    IToolAuthorizationService? toolAuthorizationService = null,
    ToolCatalogSnapshot? toolCatalogSnapshot = null,
    MemoryInvocationSnapshotStore? memorySnapshotStore = null,
    RuntimeLimits? runtimeLimits = null,
    Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        toolPermissionHandler = null,
    Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        approvalHandler = null,
    IToolStateStore? toolStateStore = null,
    Func<bool>? isRunTimedOut = null) : IModeOrchestrator
{
    private const string ProjectionVersion = "expert-full-tail-v1";
    private readonly SqliteEventOutbox _outbox = outbox
        ?? throw new ArgumentNullException(nameof(outbox));
    private readonly IRuntimeProviderAdapter _provider = provider
        ?? throw new ArgumentNullException(nameof(provider));
    private readonly SqliteSessionRepository _sessionRepo = sessionRepo
        ?? throw new ArgumentNullException(nameof(sessionRepo));
    private readonly ConversationStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly AgentContextComposer? _agentContextComposer = agentContextComposer;
    private readonly Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        _credentialRefreshHandler = credentialRefreshHandler;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _approvalHandler = approvalHandler;
    private readonly MemoryInvocationSnapshotStore? _memorySnapshotStore = memorySnapshotStore;
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
        MaxToolRounds: 8,
        MaxToolCallsPerRound: 8,
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot = toolCatalogSnapshot;
    private readonly ToolGateway? _toolGateway = toolGateway;
    private readonly IToolAuthorizationService? _toolAuthorizationService = toolAuthorizationService;
    private readonly IToolStateStore? _toolStateStore = toolStateStore;
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        _toolPermissionHandler = toolPermissionHandler;
    private readonly Func<bool>? _isRunTimedOut = isRunTimedOut;

    public RuntimeMode Mode => RuntimeMode.Expert;

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
        var expert = ValidateAndGetOptions(request);
        var providerId = expert.Agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = expert.Agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        if (!string.Equals(_provider.ProviderId, providerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not available through the selected adapter.");
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var agentContext = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(expert.Agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(expert.Agent.SystemPrompt, ct)
                .ConfigureAwait(false);
        using var memorySnapshotLease = CaptureMemorySnapshot(invocationId, agentContext);
        var catalogVersion = _toolCatalogSnapshot?.EffectiveVersion
            ?? request.Selection.ToolCatalogVersion;
        var snapshot = new InvocationSnapshot(
            invocationId,
            expert.Agent.AgentId,
            providerId,
            modelId,
            ComputeHash(expert.Agent.SystemPrompt),
            agentContext.GlobalMemoryHash,
            agentContext.ProjectMemoryHash,
            catalogVersion,
            ProjectionVersion,
            startedAt);

        await AppendContentAsync(
            sessionId,
            invocationId,
            expert.Agent.AgentId,
            "user",
            request.InitialInput,
            startedAt,
            ct).ConfigureAwait(false);
        var snapshotJson = JsonSerializer.Serialize(
            snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        await _sessionRepo.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            ct).ConfigureAwait(false);
        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var model = _provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal));
        var tokenLimit = model?.ContextWindow is > 0
            ? (int)(model.ContextWindow.Value * 0.9)
            : int.MaxValue;
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(agentContext.Instructions)]);
        ToolDescriptor[]? tools = _toolGateway is not null && _toolCatalogSnapshot is not null
            ? [.. _toolCatalogSnapshot.Tools]
            : null;
        ValueTask<ProviderTokenEstimate> EstimateProjectionAsync(
            RuntimeProviderMessage[] candidateMessages,
            CancellationToken token) =>
            _provider.EstimateTokensAsync(
                new RuntimeProviderRequest(
                    invocationId,
                    expert.Agent.AgentId,
                    providerId,
                    modelId,
                    [systemMessage, .. candidateMessages],
                    Tools: tools,
                    CancellationToken: token),
                token);
        var projection = await ContextProjectionBuilder.BuildAsync(
                history,
                tokenLimit,
                EstimateProjectionAsync,
                ct)
            .ConfigureAwait(false);
        var messages = new RuntimeProviderMessage[projection.Messages.Length + 1];
        messages[0] = systemMessage;
        projection.Messages.CopyTo(messages, 1);
        var providerMessages = messages.ToList();
        var providerRequest = new RuntimeProviderRequest(
            invocationId,
            expert.Agent.AgentId,
            providerId,
            modelId,
            [.. providerMessages],
            Tools: tools,
            CancellationToken: ct);

        long runSequence = 1;
        var startedPayload = JsonSerializer.SerializeToElement(
            new InvocationStartedEvent(invocationId, snapshot),
            RuntimeJsonContext.Default.InvocationStartedEvent);
        yield return await CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence++,
            MessageTypes.InvocationStarted,
            startedPayload,
            ct).ConfigureAwait(false);
        if (projection.IsAdjusted)
        {
            var adjustedPayload = JsonSerializer.SerializeToElement(
                new ContextProjectionAdjustedEvent(
                    invocationId,
                    projection.DroppedMessageCount,
                    projection.Messages.Length,
                    projection.EstimatedTokens,
                    projection.EstimateSource,
                    projection.Strategy),
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.ContextProjectionAdjusted,
                adjustedPayload,
                ct).ConfigureAwait(false);
        }

        var assistantContent = new List<ContentBlock>();
        var terminalContent = new List<ContentBlock>();
        RuntimeError? failure = null;
        var cancelled = false;
        var maxToolRounds = _runtimeLimits.MaxToolRounds ?? 8;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound ?? 8;
        var maxToolResultBytes = _runtimeLimits.MaxToolResultBytesPerInvocation
            ?? 4L * 1024 * 1024;
        long totalToolResultBytes = 0;
        for (var toolRound = 0; ; toolRound++)
        {
            assistantContent = [];
            var completed = false;
            for (var providerAttempt = 0; ; providerAttempt++)
            {
                completed = false;
                failure = null;
                var hasObservedProviderOutput = false;
                var attemptRequest = providerRequest.CreateAttempt(providerAttempt);
                var providerEnumerator = _provider.CompleteStreamingAsync(attemptRequest, ct)
                    .GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        RuntimeProviderEvent? providerEvent = null;
                        try
                        {
                            if (!await providerEnumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                if (!completed && failure is null)
                                {
                                    failure = CreateRuntimeError(
                                        "ProviderStreamEnded",
                                        "The Provider stream ended without a terminal event.");
                                }

                                break;
                            }

                            providerEvent = providerEnumerator.Current;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        catch (RuntimeProviderException ex)
                        {
                            failure = ex.Error;
                            break;
                        }
                        catch (Exception ex)
                        {
                            failure = CreateRuntimeError("ProviderExecutionFailed", ex.Message);
                            break;
                        }

                        switch (providerEvent)
                        {
                            case InvocationCompletedProviderEvent:
                                completed = true;
                                break;
                            case InvocationFailedProviderEvent failed:
                                failure = failed.Error;
                                break;
                            default:
                                hasObservedProviderOutput |= providerEvent is
                                    TextDeltaProviderEvent
                                    or ReasoningDeltaProviderEvent
                                    or ToolCallDeltaProviderEvent
                                    or ToolCallCompleteProviderEvent
                                    or UsageUpdatedProviderEvent;
                                AccumulateAssistantContent(assistantContent, providerEvent);
                                var mapped = MapProviderEvent(providerEvent);
                                var providerSequence = runSequence++;
                                if (IsDroppableDelta(mapped.MessageType))
                                {
                                    var envelope = await CreateDeltaEnvelopeAsync(
                                        runtimeInstanceId,
                                        runId,
                                        providerSequence,
                                        mapped.MessageType,
                                        mapped.Payload,
                                        ct).ConfigureAwait(false);
                                    if (envelope is not null)
                                    {
                                        yield return envelope;
                                    }
                                    else
                                    {
                                        var droppedPayload = JsonSerializer.SerializeToElement(
                                            new DeltaDroppedEvent(
                                                runId,
                                                providerSequence,
                                                providerSequence),
                                            RuntimeJsonContext.Default.DeltaDroppedEvent);
                                        yield return await CreateEnvelopeAsync(
                                            runtimeInstanceId,
                                            runId,
                                            runSequence++,
                                            MessageTypes.DeltaDropped,
                                            droppedPayload,
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    yield return await CreateEnvelopeAsync(
                                        runtimeInstanceId,
                                        runId,
                                        providerSequence,
                                        mapped.MessageType,
                                        mapped.Payload,
                                        ct).ConfigureAwait(false);
                                }

                                break;
                        }

                        if (completed || failure is not null)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    await providerEnumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (completed || failure is null)
                {
                    break;
                }

                if (providerAttempt != 0
                    || !IsAuthenticationFailure(failure)
                    || !ProviderRetryPolicy.CanRetry(providerRequest, hasObservedProviderOutput)
                    || _credentialRefreshHandler is null)
                {
                    break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Running,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);

                var refreshRequest = new CredentialsRefreshRequestedEvent(
                    runId,
                    providerId,
                    "Unauthorized");
                var refreshed = await _credentialRefreshHandler(refreshRequest, ct)
                    .ConfigureAwait(false);
                if (!refreshed)
                {
                    failure = CreateRuntimeError(
                        "CredentialRefreshTimeout",
                        "Provider credentials were not refreshed before the waiting period expired.");
                    break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    runId,
                    RunStatus.WaitingForCredentials,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
            }

            terminalContent.AddRange(assistantContent);
            if (cancelled || failure is not null)
            {
                break;
            }

            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                break;
            }

            if (toolRound >= maxToolRounds)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Invocation exceeded the {maxToolRounds}-round tool limit.");
                break;
            }

            if (toolCalls.Length > maxToolCallsPerRound)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Provider requested {toolCalls.Length} tools in one round; the limit is {maxToolCallsPerRound}.");
                break;
            }

            if (toolCalls.Select(static call => call.CallId).Distinct(StringComparer.Ordinal).Count()
                != toolCalls.Length)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.ToolCallConflict,
                    "The Provider returned duplicate tool call ids in one round.");
                break;
            }

            if (_toolGateway is null || _toolCatalogSnapshot is null)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.ToolExecutorUnavailable,
                    "The selected Runtime does not have a tool gateway.");
                break;
            }

            await PersistPartialAssistantAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                assistantContent).ConfigureAwait(false);
            providerMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));
            await _sessionRepo.TransitionRunStatusAsync(
                runId,
                RunStatus.Running,
                RunStatus.WaitingForTool,
                ct).ConfigureAwait(false);
            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                RunStatus.WaitingForTool,
                ct).ConfigureAwait(false);

            foreach (var toolCall in toolCalls)
            {
                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    expert.Agent.AgentId,
                    ParentAgentId: null,
                    toolCall.Arguments.GetRawText(),
                    runId,
                    sessionId,
                    invocationId,
                    ToolCatalogVersion: _toolCatalogSnapshot.EffectiveVersion);
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
                    cancelled = true;
                    break;
                }
                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsPermission
                    or ToolGatewayResultKind.NeedsApproval)
                {
                    await _sessionRepo.TransitionRunStatusAsync(
                        runId,
                        RunStatus.WaitingForTool,
                        RunStatus.WaitingForApproval,
                        ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.WaitingForApproval,
                        ct).ConfigureAwait(false);
                    gatewayResult = await ResolveConsentAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        _toolCatalogSnapshot,
                        ct).ConfigureAwait(false);
                    await _sessionRepo.TransitionRunStatusAsync(
                        runId,
                        RunStatus.WaitingForApproval,
                        RunStatus.WaitingForTool,
                        ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.WaitingForTool,
                        ct).ConfigureAwait(false);
                }

                runSequence = await _outbox.GetNextRunSequenceAsync(runId, ct)
                    .ConfigureAwait(false);

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                var resultBytes = GetContentByteCount(toolResult);
                totalToolResultBytes += resultBytes;
                if (HasOversizedInlineContent(
                        toolResult,
                        _runtimeLimits.MaxInlineContentBytes)
                    || totalToolResultBytes > maxToolResultBytes)
                {
                    failure = CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        "Tool results exceeded the configured inline or Invocation size limit.");
                    break;
                }

                await AppendContentAsync(
                    sessionId,
                    invocationId,
                    expert.Agent.AgentId,
                    RuntimeProviderRoles.Tool,
                    [toolResult],
                    DateTimeOffset.UtcNow,
                    ct).ConfigureAwait(false);
                providerMessages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
            }

            if (cancelled || failure is not null)
            {
                break;
            }

            await _sessionRepo.TransitionRunStatusAsync(
                runId,
                RunStatus.WaitingForTool,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            providerRequest = providerRequest with
            {
                Messages = [.. providerMessages],
                InternalRequestId = null,
                AttemptNumber = 0,
                IsIdempotent = false,
                HasIrreversibleToolSideEffects = true
            };
        }

        if (cancelled || failure is not null)
        {
            await PersistPartialAssistantAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                assistantContent).ConfigureAwait(false);
            if (cancelled)
            {
                var timedOut = _isRunTimedOut?.Invoke() == true;
                if (timedOut)
                {
                    failure = CreateRuntimeError(
                        RuntimeErrorCodes.RunTimedOut,
                        "The Run exceeded its configured timeout.");
                    await TransitionToTerminalAsync(
                        runId,
                        RunStatus.Failed,
                        CreateTerminalTextSnapshot(terminalContent)).ConfigureAwait(false);
                    var timeoutInvocationPayload = JsonSerializer.SerializeToElement(
                        new InvocationFailedEvent(invocationId, failure),
                        RuntimeJsonContext.Default.InvocationFailedEvent);
                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        MessageTypes.InvocationFailed,
                        timeoutInvocationPayload,
                        CancellationToken.None).ConfigureAwait(false);
                    var timeoutRunFailedPayload = JsonSerializer.SerializeToElement(
                        new RunFailedEvent(runId, sessionId, failure),
                        RuntimeJsonContext.Default.RunFailedEvent);
                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence,
                        MessageTypes.RunFailed,
                        timeoutRunFailedPayload,
                        CancellationToken.None).ConfigureAwait(false);
                    yield break;
                }

                failure ??= CreateRuntimeError(
                    "RunCancelled",
                    "The Run was cancelled before the Provider stream completed.");
                await TransitionToTerminalAsync(
                    runId,
                    RunStatus.Cancelled,
                    CreateTerminalTextSnapshot(terminalContent)).ConfigureAwait(false);
                var cancelledInvocationPayload = JsonSerializer.SerializeToElement(
                    new InvocationFailedEvent(invocationId, failure),
                    RuntimeJsonContext.Default.InvocationFailedEvent);
                yield return await CreateEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    MessageTypes.InvocationFailed,
                    cancelledInvocationPayload,
                    CancellationToken.None).ConfigureAwait(false);
                var runCancelledPayload = JsonSerializer.SerializeToElement(
                    new RunCancelledEvent(runId, sessionId),
                    RuntimeJsonContext.Default.RunCancelledEvent);
                yield return await CreateEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence,
                    MessageTypes.RunCancelled,
                    runCancelledPayload,
                    CancellationToken.None).ConfigureAwait(false);
                yield break;
            }

            await TransitionToTerminalAsync(
                runId,
                RunStatus.Failed,
                CreateTerminalTextSnapshot(terminalContent)).ConfigureAwait(false);
            var invocationFailedPayload = JsonSerializer.SerializeToElement(
                new InvocationFailedEvent(invocationId, failure!),
                RuntimeJsonContext.Default.InvocationFailedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.InvocationFailed,
                invocationFailedPayload,
                CancellationToken.None).ConfigureAwait(false);
            var runFailedPayload = JsonSerializer.SerializeToElement(
                new RunFailedEvent(runId, sessionId, failure!),
                RuntimeJsonContext.Default.RunFailedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence,
                MessageTypes.RunFailed,
                runFailedPayload,
                CancellationToken.None).ConfigureAwait(false);
            yield break;
        }

        await _sessionRepo.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            CancellationToken.None).ConfigureAwait(false);
        await PersistPartialAssistantAsync(
            sessionId,
            invocationId,
            expert.Agent.AgentId,
            assistantContent).ConfigureAwait(false);
        var completedPayload = JsonSerializer.SerializeToElement(
            new InvocationCompletedEvent(invocationId),
            RuntimeJsonContext.Default.InvocationCompletedEvent);
        yield return await CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence++,
            MessageTypes.InvocationCompleted,
            completedPayload,
            CancellationToken.None).ConfigureAwait(false);
        await TransitionToTerminalAsync(
            runId,
            RunStatus.Completed,
            CreateTerminalTextSnapshot(terminalContent)).ConfigureAwait(false);
        var runCompletedPayload = JsonSerializer.SerializeToElement(
            new RunCompletedEvent(runId, sessionId),
            RuntimeJsonContext.Default.RunCompletedEvent);
        yield return await CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunCompleted,
            runCompletedPayload,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ToolGatewayResult> ResolveConsentAndExecuteAsync(
        ToolInvocation invocation,
        ToolGatewayResult pendingResult,
        ToolCatalogSnapshot catalogSnapshot,
        CancellationToken ct)
    {
        if (_toolGateway is null || _toolAuthorizationService is null)
        {
            return FailedToolGatewayResult(
                RuntimeErrorCodes.ToolExecutorUnavailable,
                "Tool consent cannot be resolved by this Runtime.");
        }

        var coordinator = new ToolConsentCoordinator(
            _toolGateway,
            _toolAuthorizationService,
            catalogSnapshot,
            _toolPermissionHandler,
            _approvalHandler,
            _toolStateStore);
        return await coordinator.ResolveAndExecuteAsync(
            invocation,
            pendingResult,
            ct).ConfigureAwait(false);
    }

    private MemorySnapshotLease? CaptureMemorySnapshot(
        string invocationId,
        AgentContextSnapshot context)
    {
        if (_memorySnapshotStore is null)
        {
            return null;
        }

        _memorySnapshotStore.Capture(invocationId, context);
        return new MemorySnapshotLease(_memorySnapshotStore, invocationId);
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

    private static ToolGatewayResult FailedToolGatewayResult(string code, string message) =>
        new(
            ToolGatewayResultKind.Failed,
            Error: new RuntimeError(
                code,
                "tool",
                message,
                IsRetryable: false,
                ProviderDetails: null,
                Guid.NewGuid().ToString("N")));

    private static ExpertModeOptions ValidateAndGetOptions(NewSessionRunRequest request)
    {
        if (request.Mode is not RuntimeMode.Expert
            || request.Selection.Mode is not RuntimeMode.Expert
            || request.Selection.ModeOptions is not ExpertModeOptions expert)
        {
            throw new InvalidOperationException("Expert mode requires one Expert Agent definition.");
        }

        if (request.InitialInput.Length == 0)
        {
            throw new ArgumentException("Expert mode requires initial input.", nameof(request));
        }

        return expert;
    }

    private async Task AppendContentAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string role,
        ContentBlock[] content,
        DateTimeOffset timestamp,
        CancellationToken ct)
    {
        var serializedContent = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationMessageDraft(
                invocationId,
                agentId,
                role,
                serializedContent,
                timestamp),
            ct).ConfigureAwait(false);
    }

    private Task PersistPartialAssistantAsync(
        string sessionId,
        string invocationId,
        string agentId,
        List<ContentBlock> content)
    {
        return content.Count == 0
            ? Task.CompletedTask
            : AppendContentAsync(
                sessionId,
                invocationId,
                agentId,
                "assistant",
                content.ToArray(),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
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
                    var extensions = MergeProviderExtensions(
                        previousReasoning.ProviderExtensions,
                        reasoning.ProviderExtensions);
                    content[^1] = previousReasoning with
                    {
                        Content = previousReasoning.Content + reasoning.Delta,
                        ProviderExtensions = extensions
                    };
                }
                else
                {
                    content.Add(new ReasoningContentBlock(
                        reasoning.Delta,
                        reasoning.ProviderExtensions));
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

    private static ProviderExtensionData[]? MergeProviderExtensions(
        ProviderExtensionData[]? existing,
        ProviderExtensionData[]? additions)
    {
        if (additions is null or { Length: 0 })
        {
            return existing;
        }

        if (existing is null or { Length: 0 })
        {
            ProviderExtensionData.ValidateCollection(additions);
            return additions;
        }

        ProviderExtensionData[] merged = [.. existing, .. additions];
        ProviderExtensionData.ValidateCollection(merged);
        return merged;
    }

    private static (string MessageType, JsonElement Payload) MapProviderEvent(
        RuntimeProviderEvent providerEvent)
    {
        if (providerEvent is TextDeltaProviderEvent text)
        {
            return (
                MessageTypes.TextDelta,
                JsonSerializer.SerializeToElement(
                    new TextDeltaEvent(text.InvocationId, text.Delta),
                    RuntimeJsonContext.Default.TextDeltaEvent));
        }

        var messageType = providerEvent switch
        {
            ReasoningDeltaProviderEvent => MessageTypes.ReasoningDelta,
            ToolCallDeltaProviderEvent => MessageTypes.ToolCallDelta,
            ToolCallCompleteProviderEvent => MessageTypes.ToolCallCompleted,
            UsageUpdatedProviderEvent => MessageTypes.UsageUpdated,
            ProjectionAdjustedProviderEvent => MessageTypes.ProviderProjectionAdjusted,
            _ => throw new InvalidOperationException(
                $"Provider event '{providerEvent.GetType().Name}' cannot be streamed.")
        };
        return (
            messageType,
            JsonSerializer.SerializeToElement(
                providerEvent,
                RuntimeProviderJsonContext.Default.RuntimeProviderEvent));
    }

    private async Task<RuntimeEventEnvelope> CreateEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        string messageType,
        JsonElement payload,
        CancellationToken ct)
    {
        var gsn = await _outbox.AppendAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        return new RuntimeEventEnvelope(
            runtimeInstanceId,
            gsn,
            runId,
            runSequence,
            messageType,
            DateTimeOffset.UtcNow,
            payload);
    }

    private async Task<RuntimeEventEnvelope?> CreateDeltaEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        string messageType,
        JsonElement payload,
        CancellationToken ct)
    {
        var gsn = await _outbox.AppendDeltaAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        return gsn is null
            ? null
            : new RuntimeEventEnvelope(
                runtimeInstanceId,
                gsn.Value,
                runId,
                runSequence,
                messageType,
                DateTimeOffset.UtcNow,
                payload);
    }

    private Task<RuntimeEventEnvelope> CreateStatusEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus status,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new RunStatusChangedEvent(runId, status.ToString()),
            RuntimeJsonContext.Default.RunStatusChangedEvent);
        return CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunStatusChanged,
            payload,
            ct);
    }

    private async Task TransitionToTerminalAsync(
        string runId,
        RunStatus terminalStatus,
        string? terminalText)
    {
        var current = await _sessionRepo.GetRunStatusAsync(runId, CancellationToken.None)
            .ConfigureAwait(false);
        if (RunStateMachine.IsTerminal(current))
        {
            return;
        }

        await _sessionRepo.TransitionRunToTerminalAsync(
            runId,
            current,
            terminalStatus,
            terminalText,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string? CreateTerminalTextSnapshot(IReadOnlyList<ContentBlock> content)
    {
        var text = string.Concat(
            content.OfType<TextContentBlock>().Select(static block => block.Text));
        return text.Length == 0 ? null : text;
    }

    private static bool IsAuthenticationFailure(RuntimeError error) =>
        string.Equals(error.Code, RuntimeErrorCodes.AuthenticationFailed, StringComparison.Ordinal)
        || string.Equals(error.Category, "authentication", StringComparison.Ordinal);

    private static bool IsDroppableDelta(string messageType) =>
        string.Equals(messageType, MessageTypes.TextDelta, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.ReasoningDelta, StringComparison.Ordinal);

    private static RuntimeError CreateRuntimeError(string code, string message) =>
        new(
            code,
            "provider",
            message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class MemorySnapshotLease(
        MemoryInvocationSnapshotStore store,
        string invocationId) : IDisposable
    {
        private MemoryInvocationSnapshotStore? _store = store;

        public void Dispose()
        {
            Interlocked.Exchange(ref _store, null)?.Release(invocationId);
        }
    }
}
