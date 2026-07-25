using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

public sealed class ToolGateway(
    IToolCatalogStore catalogStore,
    IToolSchemaValidator schemaValidator,
    IToolAuthorizationService authorizationService,
    IEnumerable<IToolExecutor> executors,
    IToolStateStore stateStore,
    IEventOutbox outbox,
    TimeProvider? timeProvider = null,
    IToolResultBlobStore? resultBlobStore = null,
    RuntimeLimits? runtimeLimits = null) : IDisposable
{
    private readonly IToolAuthorizationService _authorizationService = authorizationService
        ?? throw new ArgumentNullException(nameof(authorizationService));
    private readonly IToolCatalogStore _catalogStore = catalogStore
        ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IToolExecutor[] _executors = executors?.ToArray()
        ?? throw new ArgumentNullException(nameof(executors));
    private readonly IEventOutbox _outbox = outbox
        ?? throw new ArgumentNullException(nameof(outbox));
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly IToolResultBlobStore? _resultBlobStore = resultBlobStore;
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits ?? new RuntimeLimits();
    private readonly IToolSchemaValidator _schemaValidator = schemaValidator
        ?? throw new ArgumentNullException(nameof(schemaValidator));
    private readonly IToolStateStore _stateStore = stateStore
        ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ToolGatewayResult> ExecuteAsync(
        ToolInvocation invocation,
        ToolPermissionContext? permission,
        CancellationToken ct = default) =>
        ExecuteAsync(invocation, permission, _catalogStore.CaptureSnapshot(), ct);

    public async Task<ToolGatewayResult> ExecuteAsync(
        ToolInvocation invocation,
        ToolPermissionContext? permission,
        ToolCatalogSnapshot catalogSnapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(catalogSnapshot);
        if (!catalogSnapshot.TryGetTool(invocation.ToolId, out var descriptor)
            || descriptor is null)
        {
            return Failed(
                RuntimeErrorCodes.ToolNotFound,
                $"Tool '{invocation.ToolId}' is not in catalog '{catalogSnapshot.EffectiveVersion}'.");
        }

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(invocation.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return Failed(RuntimeErrorCodes.ToolArgumentsInvalid, ex.Message);
        }

        using (arguments)
        {
            var validation = _schemaValidator.ValidateArguments(descriptor, arguments.RootElement);
            if (!validation.IsValid)
            {
                return Failed(
                    RuntimeErrorCodes.ToolArgumentsInvalid,
                    validation.Error ?? "Tool arguments failed JSON Schema validation.");
            }
        }

        var executor = _executors.FirstOrDefault(candidate => candidate.CanExecute(invocation.ToolId));
        if (executor is null)
        {
            return Failed(
                RuntimeErrorCodes.ToolExecutorUnavailable,
                $"No executor is available for tool '{invocation.ToolId}'.");
        }

        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ArgumentsJson)));
        var requestedPermission = permission ?? invocation.PermissionContext;
        var existingResult = await TryBeginExecutionAsync(
            invocation,
            descriptor,
            catalogSnapshot.EffectiveVersion,
            argumentsHash,
            requestedPermission,
            ct)
            .ConfigureAwait(false);
        var isIdempotentResend = false;
        if (existingResult is not null)
        {
            if (existingResult.Error?.Code != RuntimeErrorCodes.ToolCallInProgress
                || executor is not IRecoverableToolExecutor recoverableExecutor)
            {
                return existingResult;
            }

            var recovery = await TryRecoverSentAsync(
                invocation,
                descriptor,
                recoverableExecutor,
                ct).ConfigureAwait(false);
            if (recovery.Result is not null)
            {
                return recovery.Result;
            }

            isIdempotentResend = recovery.ShouldResend;
            if (!isIdempotentResend)
            {
                return existingResult;
            }
        }

        var authorization = await _authorizationService.AuthorizeAsync(
            invocation,
            descriptor,
            requestedPermission,
            ct).ConfigureAwait(false);
        if (authorization.Decision is not ToolAuthorizationDecision.Granted
            || authorization.EffectivePermission is null)
        {
            return ToAuthorizationResult(invocation, descriptor, authorization);
        }

        var effectivePermission = authorization.EffectivePermission;
        var authorizedInvocation = invocation with
        {
            PermissionContext = effectivePermission,
            ToolCatalogVersion = catalogSnapshot.EffectiveVersion,
            TimeoutMilliseconds = checked(descriptor.TimeoutSeconds * 1000)
        };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(descriptor.TimeoutSeconds));

        IPreparedToolExecution prepared;
        try
        {
            prepared = await executor.PrepareAsync(
                    authorizedInvocation,
                    timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CompleteBeforeExecutionAsync(
                invocation,
                new ToolResult(
                    invocation.CallId,
                    invocation.ToolId,
                    false,
                    Error: "Tool preparation was cancelled."),
                isIdempotentResend ? ToolIntentStatus.Sent : ToolIntentStatus.Pending,
                ToolIntentStatus.Cancelled,
                RuntimeErrorCodes.ToolExecutionCancelled,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            return await CompleteBeforeExecutionAsync(
                invocation,
                new ToolResult(
                    invocation.CallId,
                    invocation.ToolId,
                    false,
                    Error: $"Tool preparation exceeded the {descriptor.TimeoutSeconds}-second timeout."),
                isIdempotentResend ? ToolIntentStatus.Sent : ToolIntentStatus.Pending,
                ToolIntentStatus.Failed,
                RuntimeErrorCodes.ToolExecutionTimedOut,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            return await CompleteBeforeExecutionAsync(
                invocation,
                new ToolResult(
                    invocation.CallId,
                    invocation.ToolId,
                    false,
                    Error: ex.Message),
                isIdempotentResend ? ToolIntentStatus.Sent : ToolIntentStatus.Pending,
                ToolIntentStatus.Failed,
                RuntimeErrorCodes.ToolExecutionFailed,
                ct).ConfigureAwait(false);
        }

        await using var preparedScope = prepared;
        var audit = await TryWritePreparedAuditAsync(
            invocation,
            descriptor,
            catalogSnapshot.EffectiveVersion,
            argumentsHash,
            prepared.TargetSummary,
            effectivePermission,
            ct).ConfigureAwait(false);
        if (audit.Error is not null)
        {
            return await CompleteBeforeExecutionAsync(
                invocation,
                audit.Error.Result ?? new ToolResult(
                    invocation.CallId,
                    invocation.ToolId,
                    false,
                    Error: audit.Error.Error?.Message ?? "The required tool audit could not be persisted."),
                isIdempotentResend ? ToolIntentStatus.Sent : ToolIntentStatus.Pending,
                ToolIntentStatus.Failed,
                RuntimeErrorCodes.ToolAuditWriteFailed,
                ct).ConfigureAwait(false);
        }

        var sentAt = _timeProvider.GetUtcNow();
        if (!isIdempotentResend
            && !await _stateStore.TryMarkSentAsync(
                invocation.CallId,
                effectivePermission.GrantId,
                effectivePermission.ApprovalRequestId,
                sentAt,
                ct).ConfigureAwait(false))
        {
            if (audit.AuditId is not null)
            {
                await _stateStore.CompleteAuditAsync(
                    audit.AuditId,
                    "not-sent",
                    resultHash: null,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
            }

            return await GetExistingResultAsync(
                invocation,
                catalogSnapshot.EffectiveVersion,
                argumentsHash,
                ct)
                .ConfigureAwait(false);
        }

        ToolResult result;
        string? errorCode = null;
        try
        {
            result = await prepared.ExecuteAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            errorCode = ValidateResult(invocation, descriptor, result);
            if (errorCode is not null)
            {
                result = result with
                {
                    Success = false,
                    OutputJson = "{}",
                    ResultBlob = null,
                    ResultHash = null,
                    Error = "Tool result failed JSON Schema validation."
                };
            }
            else
            {
                result = await PrepareResultForPersistenceAsync(
                    invocation.RunId,
                    result,
                    timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: "Tool execution was cancelled.");
            await CompleteAsync(
                invocation,
                result,
                ToolIntentStatus.Cancelled,
                RuntimeErrorCodes.ToolExecutionCancelled,
                effectivePermission,
                audit.AuditId,
                CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            result = new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: $"Tool execution exceeded the {descriptor.TimeoutSeconds}-second timeout.");
            errorCode = RuntimeErrorCodes.ToolExecutionTimedOut;
        }
        catch (ToolResultUnavailableException ex)
        {
            if (audit.AuditId is not null)
            {
                await _stateStore.CompleteAuditAsync(
                    audit.AuditId,
                    "result-unavailable",
                    resultHash: null,
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return Failed(RuntimeErrorCodes.ToolResultUnknown, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            result = new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: ex.Message);
            errorCode = RuntimeErrorCodes.ToolExecutionFailed;
        }

        var completionErrorCode = result.Success
            ? null
            : errorCode ?? RuntimeErrorCodes.ToolExecutionFailed;
        var isVisible = await CompleteAsync(
            invocation,
            result,
            result.Success ? ToolIntentStatus.Succeeded : ToolIntentStatus.Failed,
            completionErrorCode,
            effectivePermission,
            audit.AuditId,
            ct).ConfigureAwait(false);
        if (!isVisible)
        {
            return Failed(
                RuntimeErrorCodes.ToolGrantRevoked,
                $"Grant '{effectivePermission.GrantId}' was revoked before the result could be returned.");
        }

        return result.Success
            ? new ToolGatewayResult(ToolGatewayResultKind.Success, result)
            : new ToolGatewayResult(
                ToolGatewayResultKind.Failed,
                result,
                CreateError(
                    completionErrorCode ?? RuntimeErrorCodes.ToolExecutionFailed,
                    result.Error ?? "Tool execution failed."));
    }

    public async Task<ToolGatewayResult?> RecoverSentIntentAsync(
        ToolIntentState intent,
        ToolCatalogSnapshot catalogSnapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(catalogSnapshot);

        if (intent.Status is ToolIntentStatus.Unknown)
        {
            return ToExistingResult(intent);
        }

        if (intent.Status is not ToolIntentStatus.Sent)
        {
            return null;
        }

        if (!catalogSnapshot.TryGetTool(intent.ToolId, out var descriptor)
            || descriptor is null)
        {
            return await CompleteSentAsUnknownAsync(
                intent,
                $"Tool '{intent.ToolId}' is not available while recovering sent call '{intent.CallId}'.",
                ct).ConfigureAwait(false);
        }

        var invocation = CreateRecoveryInvocation(intent, descriptor);
        var executor = _executors.FirstOrDefault(candidate => candidate.CanExecute(intent.ToolId));
        if (executor is not IRecoverableToolExecutor recoverableExecutor)
        {
            return RequiresManualIntervention(descriptor)
                ? await CompleteSentAsUnknownAsync(
                    intent,
                    $"Tool '{intent.ToolId}' cannot query the result of sent call '{intent.CallId}'.",
                    ct).ConfigureAwait(false)
                : null;
        }

        var recovery = await TryRecoverSentAsync(
            invocation,
            descriptor,
            recoverableExecutor,
            ct).ConfigureAwait(false);
        if (recovery.Result is not null)
        {
            return recovery.Result;
        }

        if (recovery.QueryUnavailable && RequiresManualIntervention(descriptor))
        {
            return await CompleteSentAsUnknownAsync(
                intent,
                $"Tool '{intent.ToolId}' could not safely determine the result of sent call '{intent.CallId}'.",
                ct).ConfigureAwait(false);
        }

        return null;
    }

    public void Dispose()
    {
        _persistenceGate.Dispose();
    }

    private async Task<ToolGatewayResult?> TryBeginExecutionAsync(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        string catalogVersion,
        string argumentsHash,
        ToolPermissionContext? permission,
        CancellationToken ct)
    {
        await _persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var workStepId = permission?.StepId ?? invocation.WorkStepId;
            var planVersion = permission?.PlanVersion ?? invocation.PlanVersion;
            if (RequiresManualIntervention(descriptor)
                && !string.IsNullOrWhiteSpace(workStepId)
                && !string.IsNullOrWhiteSpace(planVersion))
            {
                var completedWorkStepIntent = await _stateStore.GetCompletedWorkStepIntentAsync(
                    invocation.SessionId,
                    workStepId,
                    planVersion,
                    invocation.ToolId,
                    argumentsHash,
                    ct).ConfigureAwait(false);
                if (completedWorkStepIntent is not null)
                {
                    if (!string.Equals(completedWorkStepIntent.CallId, invocation.CallId, StringComparison.Ordinal))
                    {
                        return ToExistingResult(completedWorkStepIntent, invocation.CallId);
                    }
                }
            }

            var inserted = await _stateStore.TryCreateIntentAsync(
                new ToolIntentState(
                    invocation.CallId,
                    invocation.InvocationId ?? invocation.CallId,
                    invocation.RunId,
                    invocation.SessionId,
                    invocation.AgentId,
                    invocation.ParentAgentId,
                    invocation.ToolId,
                    catalogVersion,
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
                    _timeProvider.GetUtcNow(),
                    SentAt: null,
                    CompletedAt: null,
                    WorkStepId: workStepId,
                    PlanVersion: planVersion),
                ct).ConfigureAwait(false);
            if (inserted)
            {
                return null;
            }

            var existing = await _stateStore.GetIntentAsync(invocation.CallId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Tool intent '{invocation.CallId}' disappeared after a duplicate insert.");
            return ValidateExistingIdentity(existing, invocation, catalogVersion, argumentsHash)
                ?? ToExistingResult(existing);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task<ToolGatewayResult> CompleteSentAsUnknownAsync(
        ToolIntentState intent,
        string message,
        CancellationToken ct)
    {
        var invocation = new ToolInvocation(
            intent.CallId,
            intent.ToolId,
            intent.AgentId,
            intent.ParentAgentId,
            "{}",
            intent.RunId,
            intent.SessionId,
            intent.InvocationId,
            ToolCatalogVersion: intent.ToolCatalogVersion,
            WorkStepId: intent.WorkStepId,
            PlanVersion: intent.PlanVersion);
        var result = new ToolResult(
            intent.CallId,
            intent.ToolId,
            false,
            Error: message);
        return await CompleteRecoveredAsync(
            invocation,
            result,
            ToolIntentStatus.Unknown,
            RuntimeErrorCodes.ToolResultUnknown,
            ct).ConfigureAwait(false);
    }

    private static ToolInvocation CreateRecoveryInvocation(
        ToolIntentState intent,
        ToolDescriptor descriptor) =>
        new(
            intent.CallId,
            intent.ToolId,
            intent.AgentId,
            intent.ParentAgentId,
            "{}",
            intent.RunId,
            intent.SessionId,
            intent.InvocationId,
            ToolCatalogVersion: intent.ToolCatalogVersion,
            TimeoutMilliseconds: checked(descriptor.TimeoutSeconds * 1000),
            WorkStepId: intent.WorkStepId,
            PlanVersion: intent.PlanVersion);

    private static bool RequiresManualIntervention(ToolDescriptor descriptor) =>
        !descriptor.IsIdempotent || descriptor.Risk >= ToolRiskLevel.Write;

    private async Task<SentRecoveryDecision> TryRecoverSentAsync(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        IRecoverableToolExecutor executor,
        CancellationToken ct)
    {
        ToolRecoveryResult recovery;
        try
        {
            recovery = await executor.QueryResultAsync(
                invocation with
                {
                    ToolCatalogVersion = invocation.ToolCatalogVersion,
                    TimeoutMilliseconds = checked(descriptor.TimeoutSeconds * 1000)
                },
                ct).ConfigureAwait(false);
        }
        catch (ToolResultUnavailableException)
        {
            return new SentRecoveryDecision(QueryUnavailable: true);
        }

        if (recovery.Status is ToolCallStatus.Pending or ToolCallStatus.Running)
        {
            return new SentRecoveryDecision();
        }

        if (recovery.Status is ToolCallStatus.Unknown)
        {
            if (descriptor.IsIdempotent)
            {
                return new SentRecoveryDecision(ShouldResend: true);
            }

            var unknown = new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: recovery.ErrorMessage
                    ?? "The host does not know the result of this non-idempotent call.");
            var result = await CompleteRecoveredAsync(
                invocation,
                unknown,
                ToolIntentStatus.Unknown,
                RuntimeErrorCodes.ToolResultUnknown,
                ct).ConfigureAwait(false);
            return new SentRecoveryDecision(Result: result);
        }

        var recoveredResult = recovery.Result
            ?? throw new InvalidDataException(
                $"Host recovery state '{recovery.Status}' did not include a terminal result.");
        var status = recovery.Status switch
        {
            ToolCallStatus.Succeeded => ToolIntentStatus.Succeeded,
            ToolCallStatus.Failed => ToolIntentStatus.Failed,
            ToolCallStatus.Cancelled => ToolIntentStatus.Cancelled,
            _ => throw new InvalidDataException(
                $"Host recovery returned unsupported state '{recovery.Status}'.")
        };
        var validationError = ValidateResult(invocation, descriptor, recoveredResult);
        if (validationError is not null)
        {
            recoveredResult = recoveredResult with
            {
                Success = false,
                OutputJson = "{}",
                ResultBlob = null,
                ResultHash = null,
                Error = "Recovered tool result failed JSON Schema validation."
            };
            status = ToolIntentStatus.Failed;
        }
        else
        {
            recoveredResult = await PrepareResultForPersistenceAsync(
                invocation.RunId,
                recoveredResult,
                ct).ConfigureAwait(false);
        }

        var errorCode = recoveredResult.Success
            ? null
            : validationError
                ?? recovery.ErrorCode
                ?? (status is ToolIntentStatus.Cancelled
                    ? RuntimeErrorCodes.ToolExecutionCancelled
                    : RuntimeErrorCodes.ToolExecutionFailed);
        var completed = await CompleteRecoveredAsync(
            invocation,
            recoveredResult,
            status,
            errorCode,
            ct).ConfigureAwait(false);
        return new SentRecoveryDecision(Result: completed);
    }

    private async Task<ToolGatewayResult> CompleteRecoveredAsync(
        ToolInvocation invocation,
        ToolResult result,
        ToolIntentStatus status,
        string? errorCode,
        CancellationToken ct)
    {
        var existing = await _stateStore.GetIntentAsync(invocation.CallId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Tool intent '{invocation.CallId}' disappeared during recovery.");
        var isVisible = existing.GrantId is null
            || await IsGrantVisibleAsync(existing.GrantId, ct).ConfigureAwait(false);
        var resultHash = GetResultHash(result);
        var completed = await _stateStore.TryCompleteIntentAsync(
            new ToolIntentCompletion(
                invocation.CallId,
                ToolIntentStatus.Sent,
                status,
                result.OutputJson,
                resultHash,
                ResultBlob: ToBlobState(result.ResultBlob),
                ErrorCode: errorCode,
                ErrorMessage: result.Error,
                IsResultVisible: isVisible,
                CompletedAt: _timeProvider.GetUtcNow()),
            ct).ConfigureAwait(false);
        if (!completed)
        {
            return await GetExistingResultAsync(
                invocation,
                existing.ToolCatalogVersion,
                existing.ArgumentsHash,
                ct).ConfigureAwait(false);
        }

        var eventName = status is ToolIntentStatus.Succeeded
            ? "tool.call.succeeded"
            : status is ToolIntentStatus.Cancelled
                ? "tool.call.cancelled"
                : "tool.call.failed";
        var runSequence = await _outbox.GetNextRunSequenceAsync(invocation.RunId, ct)
            .ConfigureAwait(false);
        await _outbox.AppendAsync(
            invocation.RunId,
            runSequence,
            eventName,
            CreateEventPayload(result),
            ct).ConfigureAwait(false);

        if (!isVisible)
        {
            return Failed(
                RuntimeErrorCodes.ToolGrantRevoked,
                $"The result for tool call '{invocation.CallId}' is no longer visible.");
        }

        return status switch
        {
            ToolIntentStatus.Succeeded => new ToolGatewayResult(
                ToolGatewayResultKind.Success,
                result),
            ToolIntentStatus.Unknown => Failed(
                RuntimeErrorCodes.ToolResultUnknown,
                result.Error ?? "The host tool result is unknown."),
            ToolIntentStatus.Cancelled => Failed(
                RuntimeErrorCodes.ToolExecutionCancelled,
                result.Error ?? "The host tool call was cancelled."),
            _ => new ToolGatewayResult(
                ToolGatewayResultKind.Failed,
                result,
                CreateError(
                    errorCode ?? RuntimeErrorCodes.ToolExecutionFailed,
                    result.Error ?? "The host tool call failed."))
        };
    }

    private async Task<ToolGatewayResult> CompleteBeforeExecutionAsync(
        ToolInvocation invocation,
        ToolResult result,
        ToolIntentStatus expectedStatus,
        ToolIntentStatus status,
        string errorCode,
        CancellationToken ct)
    {
        var resultHash = GetResultHash(result);
        var eventName = status is ToolIntentStatus.Cancelled
            ? "tool.call.cancelled"
            : "tool.call.failed";

        await _persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completed = await _stateStore.TryCompleteIntentAsync(
                new ToolIntentCompletion(
                    invocation.CallId,
                    expectedStatus,
                    status,
                    result.OutputJson,
                    resultHash,
                    ResultBlob: ToBlobState(result.ResultBlob),
                    ErrorCode: errorCode,
                    ErrorMessage: result.Error,
                    IsResultVisible: true,
                    CompletedAt: _timeProvider.GetUtcNow()),
                ct).ConfigureAwait(false);
            if (!completed)
            {
                var existing = await _stateStore.GetIntentAsync(invocation.CallId, ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Tool intent '{invocation.CallId}' disappeared during preparation.");
                return ToExistingResult(existing)
                    ?? Failed(
                        RuntimeErrorCodes.ToolCallInProgress,
                        $"Tool call '{invocation.CallId}' is already in progress.");
            }

            var runSequence = await _outbox.GetNextRunSequenceAsync(invocation.RunId, ct)
                .ConfigureAwait(false);
            await _outbox.AppendAsync(
                invocation.RunId,
                runSequence,
                eventName,
                CreateEventPayload(result),
                ct).ConfigureAwait(false);
        }
        finally
        {
            _persistenceGate.Release();
        }

        return new ToolGatewayResult(
            ToolGatewayResultKind.Failed,
            result,
            CreateError(errorCode, result.Error ?? "Tool preparation failed."));
    }

    private async Task<bool> CompleteAsync(
        ToolInvocation invocation,
        ToolResult result,
        ToolIntentStatus status,
        string? errorCode,
        ToolPermissionContext permission,
        string? auditId,
        CancellationToken ct)
    {
        var resultHash = GetResultHash(result);
        var isVisible = await IsResultVisibleAsync(permission, ct).ConfigureAwait(false);
        var eventName = status switch
        {
            ToolIntentStatus.Succeeded => "tool.call.succeeded",
            ToolIntentStatus.Cancelled => "tool.call.cancelled",
            _ => "tool.call.failed"
        };
        var payload = CreateEventPayload(result);

        await _persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completed = await _stateStore.TryCompleteIntentAsync(
                new ToolIntentCompletion(
                    result.CallId,
                    ToolIntentStatus.Sent,
                    status,
                    result.OutputJson,
                    resultHash,
                    ResultBlob: ToBlobState(result.ResultBlob),
                    ErrorCode: errorCode,
                    ErrorMessage: result.Error,
                    IsResultVisible: isVisible,
                    CompletedAt: _timeProvider.GetUtcNow()),
                ct).ConfigureAwait(false);
            if (!completed)
            {
                var existing = await _stateStore.GetIntentAsync(result.CallId, ct)
                    .ConfigureAwait(false);
                if (existing?.Status != status)
                {
                    throw new InvalidOperationException(
                        $"Tool call '{result.CallId}' is no longer in the Sent state and has conflicting status '{existing?.Status}'.");
                }

                return existing.IsResultVisible;
            }

            if (auditId is not null)
            {
                await _stateStore.CompleteAuditAsync(
                    auditId,
                    status.ToString().ToLowerInvariant(),
                    resultHash,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
            }

            var runSequence = await _outbox.GetNextRunSequenceAsync(invocation.RunId, ct)
                .ConfigureAwait(false);
            await _outbox.AppendAsync(
                invocation.RunId,
                runSequence,
                eventName,
                payload,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _persistenceGate.Release();
        }

        return isVisible;
    }

    private async Task<(string? AuditId, ToolGatewayResult? Error)> TryWritePreparedAuditAsync(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        string catalogVersion,
        string argumentsHash,
        string targetSummary,
        ToolPermissionContext permission,
        CancellationToken ct)
    {
        if (descriptor.Risk < ToolRiskLevel.Destructive)
        {
            return (null, null);
        }

        var auditId = Guid.NewGuid().ToString("N");
        var rootGrantId = permission.RootGrantId
            ?? permission.DelegationChain?.FirstOrDefault()
            ?? permission.GrantId;
        try
        {
            await _stateStore.AppendAuditAsync(
                new ToolAuditRecord(
                    auditId,
                    invocation.CallId,
                    invocation.RunId,
                    invocation.SessionId,
                    invocation.InvocationId ?? invocation.CallId,
                    invocation.ParentInvocationId ?? permission.ParentInvocationId,
                    invocation.AgentId,
                    invocation.ParentAgentId,
                    invocation.ToolId,
                    catalogVersion,
                    (int)descriptor.Risk,
                    argumentsHash,
                    targetSummary,
                    permission.GrantId,
                    rootGrantId,
                    permission.ApprovalRequestId,
                    permission.DelegationChain ?? [],
                    "prepared",
                    ResultHash: null,
                    DiagnosticId: Guid.NewGuid().ToString("N"),
                    CreatedAt: _timeProvider.GetUtcNow(),
                    WorkStepId: invocation.WorkStepId ?? permission.StepId,
                    PlanVersion: invocation.PlanVersion ?? permission.PlanVersion),
                ct).ConfigureAwait(false);
            return (auditId, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (
                null,
                Failed(
                    RuntimeErrorCodes.ToolAuditWriteFailed,
                    $"The required tool audit could not be persisted: {ex.Message}"));
        }
    }

    private async Task<ToolGatewayResult> GetExistingResultAsync(
        ToolInvocation invocation,
        string catalogVersion,
        string argumentsHash,
        CancellationToken ct)
    {
        var existing = await _stateStore.GetIntentAsync(invocation.CallId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Tool intent '{invocation.CallId}' disappeared during execution.");
        return ValidateExistingIdentity(existing, invocation, catalogVersion, argumentsHash)
            ?? ToExistingResult(existing)
            ?? Failed(
                RuntimeErrorCodes.ToolCallInProgress,
                $"Tool call '{invocation.CallId}' is waiting to be sent.",
                isRetryable: true);
    }

    private async Task<bool> IsResultVisibleAsync(
        ToolPermissionContext permission,
        CancellationToken ct) =>
        await IsGrantVisibleAsync(permission.GrantId, ct).ConfigureAwait(false);

    private async Task<bool> IsGrantVisibleAsync(
        string grantId,
        CancellationToken ct)
    {
        var grant = await _stateStore.GetGrantAsync(grantId, ct).ConfigureAwait(false);
        return grant is null
            || (grant.RevokedAt is null && grant.ExpiresAt > _timeProvider.GetUtcNow());
    }

    private string? ValidateResult(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.CallId, invocation.CallId, StringComparison.Ordinal)
            || !string.Equals(result.ToolId, invocation.ToolId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The tool executor returned a result for another tool call.");
        }

        if (result.OutputJson is null)
        {
            if (result.ResultBlob is null)
            {
                return result.Success ? RuntimeErrorCodes.ToolResultInvalid : null;
            }

            return result.ResultHash is null
                || string.Equals(
                    result.ResultHash,
                    result.ResultBlob.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                    ? null
                    : RuntimeErrorCodes.ToolResultInvalid;
        }

        var actualHash = ComputeHash(result.OutputJson);
        if (result.ResultHash is not null
            && !string.Equals(result.ResultHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeErrorCodes.ToolResultInvalid;
        }

        using var output = JsonDocument.Parse(result.OutputJson);
        if (!result.Success)
        {
            return null;
        }

        var validation = _schemaValidator.ValidateResult(descriptor, output.RootElement);
        return validation.IsValid ? null : RuntimeErrorCodes.ToolResultInvalid;
    }

    private static ToolGatewayResult? ValidateExistingIdentity(
        ToolIntentState existing,
        ToolInvocation invocation,
        string catalogVersion,
        string argumentsHash)
    {
        return !string.Equals(existing.ToolId, invocation.ToolId, StringComparison.Ordinal)
            || !string.Equals(
                existing.InvocationId,
                invocation.InvocationId ?? invocation.CallId,
                StringComparison.Ordinal)
            || !string.Equals(existing.RunId, invocation.RunId, StringComparison.Ordinal)
            || !string.Equals(existing.SessionId, invocation.SessionId, StringComparison.Ordinal)
            || !string.Equals(existing.AgentId, invocation.AgentId, StringComparison.Ordinal)
            || !string.Equals(existing.ParentAgentId, invocation.ParentAgentId, StringComparison.Ordinal)
            || !string.Equals(existing.ToolCatalogVersion, catalogVersion, StringComparison.Ordinal)
            || !string.Equals(existing.ArgumentsHash, argumentsHash, StringComparison.Ordinal)
                ? Failed(
                    RuntimeErrorCodes.ToolCallConflict,
                    $"Tool call id '{invocation.CallId}' is already bound to another invocation.")
                : null;
    }

    private static ToolGatewayResult? ToExistingResult(
        ToolIntentState existing,
        string? resultCallId = null) =>
        existing.Status switch
        {
            ToolIntentStatus.Pending => null,
            ToolIntentStatus.Sent => Failed(
                RuntimeErrorCodes.ToolCallInProgress,
                $"Tool call '{existing.CallId}' was sent and is still in progress.",
                isRetryable: true),
            ToolIntentStatus.Succeeded when existing.IsResultVisible
                && (existing.ResultJson is not null || existing.ResultBlob is not null) =>
                new ToolGatewayResult(
                ToolGatewayResultKind.Success,
                new ToolResult(
                    resultCallId ?? existing.CallId,
                    existing.ToolId,
                    true,
                    existing.ResultJson,
                    ResultBlob: ToBlobReference(existing.ResultBlob),
                    ResultHash: existing.ResultHash)),
            ToolIntentStatus.Succeeded when existing.IsResultVisible => Failed(
                RuntimeErrorCodes.ToolIntentInvalid,
                $"The persisted result for tool call '{existing.CallId}' is incomplete."),
            ToolIntentStatus.Succeeded => Failed(
                RuntimeErrorCodes.ToolGrantRevoked,
                $"The result for tool call '{existing.CallId}' is no longer visible."),
            ToolIntentStatus.Failed => new ToolGatewayResult(
                ToolGatewayResultKind.Failed,
                new ToolResult(
                    resultCallId ?? existing.CallId,
                    existing.ToolId,
                    false,
                    existing.ResultJson ?? "{}",
                    existing.ErrorMessage,
                    ToBlobReference(existing.ResultBlob),
                    existing.ResultHash),
                CreateError(
                    existing.ErrorCode ?? RuntimeErrorCodes.ToolExecutionFailed,
                    existing.ErrorMessage ?? "Tool execution failed.")),
            ToolIntentStatus.Cancelled => Failed(
                RuntimeErrorCodes.ToolExecutionCancelled,
                existing.ErrorMessage ?? $"Tool call '{existing.CallId}' was cancelled."),
            ToolIntentStatus.Unknown => Failed(
                RuntimeErrorCodes.ToolResultUnknown,
                $"The result of tool call '{existing.CallId}' is unknown and requires manual intervention."),
            _ => Failed(
                RuntimeErrorCodes.ToolIntentInvalid,
                $"Tool call '{existing.CallId}' has unsupported status '{existing.Status}'.")
        };

    private static ToolGatewayResult ToAuthorizationResult(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolAuthorizationResult authorization) =>
        authorization.Decision is ToolAuthorizationDecision.NeedsApproval
            ? new ToolGatewayResult(
                ToolGatewayResultKind.NeedsApproval,
                Error: CreateError(
                    RuntimeErrorCodes.ToolApprovalRequired,
                    authorization.Reason
                        ?? $"Tool '{descriptor.ToolId}' requires approval for call '{invocation.CallId}'."))
            : new ToolGatewayResult(
                ToolGatewayResultKind.NeedsPermission,
                Error: CreateError(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    authorization.Reason
                        ?? $"Tool '{descriptor.ToolId}' requires a valid Grant."));

    private async Task<ToolResult> PrepareResultForPersistenceAsync(
        string runId,
        ToolResult result,
        CancellationToken ct)
    {
        if (result.ResultBlob is not null)
        {
            if (_resultBlobStore is null)
            {
                throw new InvalidOperationException(
                    "A tool returned a Blob result but no result Blob store is configured.");
            }

            await _resultBlobStore.ValidateReferenceAsync(result.ResultBlob, ct)
                .ConfigureAwait(false);
        }

        if (result.OutputJson is null)
        {
            if (result.ResultBlob is null)
            {
                throw new InvalidDataException("A tool result did not contain inline or Blob content.");
            }

            var blobHash = result.ResultBlob.Sha256;
            if (result.ResultHash is not null
                && !string.Equals(result.ResultHash, blobHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The tool result hash does not match its Blob content.");
            }

            return result with { ResultHash = blobHash };
        }

        var bytes = Encoding.UTF8.GetBytes(result.OutputJson);
        var resultHash = ComputeHash(result.OutputJson);
        if (result.ResultHash is not null
            && !string.Equals(result.ResultHash, resultHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The tool result hash does not match its inline content.");
        }

        if (bytes.Length <= _runtimeLimits.MaxInlineContentBytes)
        {
            return result with { ResultHash = resultHash };
        }

        if (result.ResultBlob is not null)
        {
            throw new InvalidDataException(
                "A tool result cannot contain both an oversized inline result and a Blob result.");
        }

        if (_resultBlobStore is null)
        {
            throw new InvalidOperationException(
                "The tool result exceeds the inline limit and no result Blob store is configured.");
        }

        var blob = await _resultBlobStore.StoreAsync(
            runId,
            bytes,
            "application/json",
            "run",
            _timeProvider.GetUtcNow().AddDays(7),
            ct).ConfigureAwait(false);
        return result with
        {
            OutputJson = null,
            ResultBlob = blob,
            ResultHash = resultHash
        };
    }

    private static string GetResultHash(ToolResult result) =>
        result.ResultHash
        ?? (result.OutputJson is not null
            ? ComputeHash(result.OutputJson)
            : result.ResultBlob?.Sha256
                ?? throw new InvalidDataException("A tool result has no hashable content."));

    private static ToolResultBlobState? ToBlobState(BlobReference? blob) =>
        blob is null
            ? null
            : new ToolResultBlobState(
                blob.BlobId,
                blob.Length,
                blob.Sha256,
                blob.ContentType,
                blob.AccessScope,
                blob.ExpiresAt);

    private static BlobReference? ToBlobReference(ToolResultBlobState? blob) =>
        blob is null
            ? null
            : new BlobReference(
                blob.BlobId,
                blob.Length,
                blob.Sha256,
                blob.ContentType,
                blob.AccessScope,
                blob.ExpiresAt);

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string CreateEventPayload(ToolResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("callId", result.CallId);
            writer.WriteString("toolId", result.ToolId);
            writer.WriteBoolean("success", result.Success);
            writer.WritePropertyName("output");
            if (result.OutputJson is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteRawValue(result.OutputJson);
            }

            if (result.ResultHash is not null)
            {
                writer.WriteString("resultHash", result.ResultHash);
            }

            if (result.ResultBlob is { } blob)
            {
                writer.WritePropertyName("resultBlob");
                writer.WriteStartObject();
                writer.WriteString("blobId", blob.BlobId);
                writer.WriteNumber("length", blob.Length);
                writer.WriteString("sha256", blob.Sha256);
                writer.WriteString("contentType", blob.ContentType);
                writer.WriteString("accessScope", blob.AccessScope);
                writer.WriteString("expiresAt", blob.ExpiresAt);
                writer.WriteEndObject();
            }
            if (result.Error is not null)
            {
                writer.WriteString("error", result.Error);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ToolGatewayResult Failed(
        string code,
        string message,
        bool isRetryable = false) =>
        new(
            ToolGatewayResultKind.Failed,
            Error: CreateError(code, message, isRetryable));

    private static RuntimeError CreateError(
        string code,
        string message,
        bool isRetryable = false) =>
        new(
            code,
            "tool",
            message,
            isRetryable,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));

    private sealed record SentRecoveryDecision(
        bool ShouldResend = false,
        ToolGatewayResult? Result = null,
        bool QueryUnavailable = false);
}
