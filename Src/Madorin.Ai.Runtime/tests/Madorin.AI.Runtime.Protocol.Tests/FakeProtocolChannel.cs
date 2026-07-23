using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

internal enum FakeControlOperation
{
    Initialize,
    StartNewSessionRun,
    StartExistingSessionRun,
    AcknowledgeEvents,
    QueryRun,
    CancelRun,
    UpdateSelection,
    Rehydrate
}

internal sealed class FakeControlRequest(
    FakeControlOperation operation,
    string targetRuntimeInstanceId,
    object? payload)
{
    public FakeControlOperation Operation { get; } = operation;

    public string TargetRuntimeInstanceId { get; } = targetRuntimeInstanceId;

    public object? Payload { get; } = payload;

    public TaskCompletionSource<FakeControlResponse> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record FakeControlResponse(
    object? Value,
    RuntimeError? Error);

internal sealed record FakeOperationResult<T>(
    T? Value,
    RuntimeError? Error)
    where T : class;

internal sealed record FakeRunStartResult(
    string SessionId,
    string RunId,
    RuntimeMode Mode,
    long FirstGsn,
    long LastGsn,
    bool IsDuplicate);

internal sealed record FakeRunState(
    string SessionId,
    string RunId,
    RuntimeMode Mode,
    RunStatus Status,
    int TerminalEventCount);

internal sealed record FakeAcknowledgeResult(
    long LastConfirmedGsn,
    int ReleasedEventCount);

internal sealed record FakeSelectionUpdate(
    string SessionId,
    int ExpectedSelectionVersion,
    NextTurnSelection Selection);

internal sealed record FakeRehydrateRequest(
    string SessionId,
    string AgentId,
    string PromptHash);

internal sealed record FakeExistingRunStart(
    ExistingSessionRunRequest Request,
    bool DuplicateDelivery);

internal sealed record FakeNewRunStart(
    NewSessionRunRequest Request,
    bool DuplicateDelivery);

internal sealed record FakeRuntimeEndpoint(
    string RuntimeInstanceId,
    ChannelWriter<FakeControlRequest> ControlWriter,
    ChannelReader<RuntimeEventEnvelope> EventReader);

internal sealed record FakeHostReceiveResult(
    IReadOnlyList<RuntimeEventEnvelope> Events,
    long LastConfirmedGsn,
    int DeliveryCount,
    RuntimeError? Error);

internal sealed class FakeHost(string hostInstanceId)
{
    private readonly Dictionary<string, FakeRuntimeEndpoint> _connections =
        new(StringComparer.Ordinal);
    private readonly HashSet<(string RuntimeInstanceId, long Gsn)> _seenEvents = [];
    private readonly Dictionary<(string RuntimeInstanceId, string RunId), List<RuntimeEventEnvelope>>
        _runEvents = [];

    public int KnownRunCount => _runEvents.Count;

    public void Connect(FakeRuntime runtime)
    {
        _connections.Add(runtime.RuntimeInstanceId, runtime.Endpoint);
    }

    public Task<FakeOperationResult<InitializeResponse>> InitializeAsync(
        string connectionRuntimeInstanceId,
        long lastConfirmedGsn = 0,
        CancellationToken cancellationToken = default)
    {
        var request = new InitializeRequest(
            hostInstanceId,
            "1.0.0-test",
            ProtocolVersions.Supported,
            new RuntimeCapabilities(),
            lastConfirmedGsn);

        return SendAsync<InitializeResponse>(
            connectionRuntimeInstanceId,
            connectionRuntimeInstanceId,
            FakeControlOperation.Initialize,
            request,
            cancellationToken);
    }

    public Task<FakeOperationResult<FakeRunStartResult>> StartNewSessionRunAsync(
        string connectionRuntimeInstanceId,
        NewSessionRunRequest request,
        bool duplicateDelivery = false,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<FakeRunStartResult>(
            connectionRuntimeInstanceId,
            connectionRuntimeInstanceId,
            FakeControlOperation.StartNewSessionRun,
            new FakeNewRunStart(request, duplicateDelivery),
            cancellationToken);
    }

    public Task<FakeOperationResult<FakeRunStartResult>> StartExistingSessionRunAsync(
        string connectionRuntimeInstanceId,
        ExistingSessionRunRequest request,
        bool duplicateDelivery = false,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<FakeRunStartResult>(
            connectionRuntimeInstanceId,
            connectionRuntimeInstanceId,
            FakeControlOperation.StartExistingSessionRun,
            new FakeExistingRunStart(request, duplicateDelivery),
            cancellationToken);
    }

    public Task<FakeOperationResult<FakeAcknowledgeResult>> AcknowledgeAsync(
        string connectionRuntimeInstanceId,
        long lastConfirmedGsn,
        string? targetRuntimeInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<FakeAcknowledgeResult>(
            connectionRuntimeInstanceId,
            targetRuntimeInstanceId ?? connectionRuntimeInstanceId,
            FakeControlOperation.AcknowledgeEvents,
            new EventAcknowledgeParameters(lastConfirmedGsn),
            cancellationToken);
    }

    public Task<FakeOperationResult<FakeRunState>> QueryRunAsync(
        string connectionRuntimeInstanceId,
        string runId,
        string? targetRuntimeInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<FakeRunState>(
            connectionRuntimeInstanceId,
            targetRuntimeInstanceId ?? connectionRuntimeInstanceId,
            FakeControlOperation.QueryRun,
            new RunCancelParameters(runId),
            cancellationToken);
    }

    public Task<FakeOperationResult<FakeRunState>> CancelRunAsync(
        string connectionRuntimeInstanceId,
        string runId,
        string? targetRuntimeInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<FakeRunState>(
            connectionRuntimeInstanceId,
            targetRuntimeInstanceId ?? connectionRuntimeInstanceId,
            FakeControlOperation.CancelRun,
            new RunCancelParameters(runId),
            cancellationToken);
    }

    public Task<FakeOperationResult<NextTurnSelection>> UpdateSelectionAsync(
        string connectionRuntimeInstanceId,
        FakeSelectionUpdate update,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<NextTurnSelection>(
            connectionRuntimeInstanceId,
            connectionRuntimeInstanceId,
            FakeControlOperation.UpdateSelection,
            update,
            cancellationToken);
    }

    public Task<FakeOperationResult<AgentRef>> RehydrateAsync(
        string connectionRuntimeInstanceId,
        FakeRehydrateRequest request,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<AgentRef>(
            connectionRuntimeInstanceId,
            connectionRuntimeInstanceId,
            FakeControlOperation.Rehydrate,
            request,
            cancellationToken);
    }

    public async Task<FakeHostReceiveResult> ReceiveRunAsync(
        string connectionRuntimeInstanceId,
        FakeRunStartResult run,
        int expectedDeliveriesPerEvent = 1,
        CancellationToken cancellationToken = default)
    {
        var endpoint = GetEndpoint(connectionRuntimeInstanceId);
        var deliveryCounts = new Dictionary<long, int>();
        var deliveryCount = 0;

        while (!AllExpectedEventsReceived(
                   deliveryCounts,
                   run.FirstGsn,
                   run.LastGsn,
                   expectedDeliveriesPerEvent))
        {
            var runtimeEvent = await endpoint.EventReader.ReadAsync(cancellationToken);
            deliveryCount++;

            if (!string.Equals(
                    runtimeEvent.RuntimeInstanceId,
                    connectionRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return new FakeHostReceiveResult(
                    [],
                    run.FirstGsn - 1,
                    deliveryCount,
                    CreateError(
                        RuntimeErrorCodes.InstanceMismatch,
                        "The event belongs to a different Runtime instance."));
            }

            if (runtimeEvent.Gsn < run.FirstGsn || runtimeEvent.Gsn > run.LastGsn)
            {
                continue;
            }

            deliveryCounts[runtimeEvent.Gsn] =
                deliveryCounts.GetValueOrDefault(runtimeEvent.Gsn) + 1;

            var eventKey = (runtimeEvent.RuntimeInstanceId, runtimeEvent.Gsn);
            if (!_seenEvents.Add(eventKey))
            {
                continue;
            }

            var runKey = (runtimeEvent.RuntimeInstanceId, runtimeEvent.RunId);
            if (!_runEvents.TryGetValue(runKey, out var events))
            {
                events = [];
                _runEvents.Add(runKey, events);
            }

            events.Add(runtimeEvent);
        }

        var receivedEvents = GetRunEvents(connectionRuntimeInstanceId, run.RunId)
            .Where(runtimeEvent =>
                runtimeEvent.Gsn >= run.FirstGsn && runtimeEvent.Gsn <= run.LastGsn)
            .ToArray();

        return new FakeHostReceiveResult(
            receivedEvents,
            run.LastGsn,
            deliveryCount,
            null);
    }

    public async Task<FakeHostReceiveResult> ReceiveSingleAsync(
        string connectionRuntimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        var endpoint = GetEndpoint(connectionRuntimeInstanceId);
        var runtimeEvent = await endpoint.EventReader.ReadAsync(cancellationToken);

        if (!string.Equals(
                runtimeEvent.RuntimeInstanceId,
                connectionRuntimeInstanceId,
                StringComparison.Ordinal))
        {
            return new FakeHostReceiveResult(
                [],
                0,
                1,
                CreateError(
                    RuntimeErrorCodes.InstanceMismatch,
                    "The event belongs to a different Runtime instance."));
        }

        return new FakeHostReceiveResult([runtimeEvent], runtimeEvent.Gsn, 1, null);
    }

    public IReadOnlyList<RuntimeEventEnvelope> GetRunEvents(
        string runtimeInstanceId,
        string runId)
    {
        return _runEvents.GetValueOrDefault((runtimeInstanceId, runId)) ?? [];
    }

    private static bool AllExpectedEventsReceived(
        IReadOnlyDictionary<long, int> deliveryCounts,
        long firstGsn,
        long lastGsn,
        int expectedDeliveriesPerEvent)
    {
        for (var gsn = firstGsn; gsn <= lastGsn; gsn++)
        {
            if (deliveryCounts.GetValueOrDefault(gsn) < expectedDeliveriesPerEvent)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<FakeOperationResult<T>> SendAsync<T>(
        string connectionRuntimeInstanceId,
        string targetRuntimeInstanceId,
        FakeControlOperation operation,
        object? payload,
        CancellationToken cancellationToken)
        where T : class
    {
        var endpoint = GetEndpoint(connectionRuntimeInstanceId);
        var request = new FakeControlRequest(operation, targetRuntimeInstanceId, payload);

        await endpoint.ControlWriter.WriteAsync(request, cancellationToken);
        var response = await request.Completion.Task.WaitAsync(cancellationToken);

        return new FakeOperationResult<T>(response.Value as T, response.Error);
    }

    private FakeRuntimeEndpoint GetEndpoint(string runtimeInstanceId)
    {
        return _connections.TryGetValue(runtimeInstanceId, out var endpoint)
            ? endpoint
            : throw new InvalidOperationException(
                $"Runtime '{runtimeInstanceId}' is not connected to this Fake Host.");
    }

    private static RuntimeError CreateError(string code, string message)
    {
        return new RuntimeError(
            code,
            "Protocol",
            message,
            false,
            null,
            $"fake-host-{code}");
    }
}

internal sealed class FakeRuntime : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTimestamp =
        new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    private readonly Channel<FakeControlRequest> _controlChannel =
        Channel.CreateUnbounded<FakeControlRequest>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    private readonly Channel<RuntimeEventEnvelope> _eventChannel =
        Channel.CreateUnbounded<RuntimeEventEnvelope>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
    private readonly Dictionary<string, FakeSession> _sessionsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeSession> _sessionsByIdempotencyKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeRun> _runsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeRun> _runsByIdempotencyKey =
        new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, RuntimeEventEnvelope> _eventBuffer = [];
    private readonly Task _controlLoop;
    private long _nextGsn;
    private int _nextSessionId;
    private int _nextRunId;

    public FakeRuntime(string runtimeInstanceId)
    {
        RuntimeInstanceId = runtimeInstanceId;
        Endpoint = new FakeRuntimeEndpoint(
            runtimeInstanceId,
            _controlChannel.Writer,
            _eventChannel.Reader);
        _controlLoop = ProcessControlLoopAsync();
    }

    public string RuntimeInstanceId { get; }

    public FakeRuntimeEndpoint Endpoint { get; }

    public long LastConfirmedGsn { get; private set; }

    public int BufferedEventCount => _eventBuffer.Count;

    public int SessionCount => _sessionsById.Count;

    public int RunCount => _runsById.Count;

    public Func<string, InvocationSnapshot, FakeSelectionUpdate?>?
        SelectionUpdateAfterSnapshotCreated { get; set; }

    public async ValueTask DisposeAsync()
    {
        _controlChannel.Writer.TryComplete();
        await _controlLoop;
        _eventChannel.Writer.TryComplete();
    }

    public ValueTask InjectEventAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        return _eventChannel.Writer.WriteAsync(runtimeEvent, cancellationToken);
    }

    public FakeRunState GetRunState(string runId)
    {
        var run = GetRun(runId);
        return run.ToState();
    }

    private async Task ProcessControlLoopAsync()
    {
        await foreach (var request in _controlChannel.Reader.ReadAllAsync())
        {
            FakeControlResponse response;

            try
            {
                response = await HandleAsync(request);
            }
            catch (Exception exception)
            {
                response = Failure(
                    "FakeRuntimeFailure",
                    $"Fake Runtime failed to process {request.Operation}: {exception.Message}");
            }

            request.Completion.TrySetResult(response);
        }
    }

    private async ValueTask<FakeControlResponse> HandleAsync(FakeControlRequest request)
    {
        if (!string.Equals(
                request.TargetRuntimeInstanceId,
                RuntimeInstanceId,
                StringComparison.Ordinal))
        {
            return Failure(
                RuntimeErrorCodes.InstanceMismatch,
                "The control request targets a different Runtime instance.");
        }

        return request.Operation switch
        {
            FakeControlOperation.Initialize =>
                HandleInitialize(RequirePayload<InitializeRequest>(request)),
            FakeControlOperation.StartNewSessionRun =>
                await HandleStartNewSessionRunAsync(
                    RequirePayload<FakeNewRunStart>(request)),
            FakeControlOperation.StartExistingSessionRun =>
                await HandleStartExistingSessionRunAsync(
                    RequirePayload<FakeExistingRunStart>(request)),
            FakeControlOperation.AcknowledgeEvents =>
                HandleAcknowledge(RequirePayload<EventAcknowledgeParameters>(request)),
            FakeControlOperation.QueryRun =>
                HandleQuery(RequirePayload<RunCancelParameters>(request)),
            FakeControlOperation.CancelRun =>
                HandleCancel(RequirePayload<RunCancelParameters>(request)),
            FakeControlOperation.UpdateSelection =>
                HandleSelectionUpdate(RequirePayload<FakeSelectionUpdate>(request)),
            FakeControlOperation.Rehydrate =>
                HandleRehydrate(RequirePayload<FakeRehydrateRequest>(request)),
            _ => Failure("UnknownFakeOperation", "The Fake Runtime operation is unknown.")
        };
    }

    private FakeControlResponse HandleInitialize(InitializeRequest request)
    {
        var selectedProtocolVersion =
            ProtocolVersions.Negotiate(request.SupportedProtocolVersions);

        if (selectedProtocolVersion is null)
        {
            return Failure(
                RuntimeErrorCodes.ProtocolNoIntersection,
                "Host and Runtime do not share a protocol version.");
        }

        return Success(
            new InitializeResponse(
                RuntimeInstanceId,
                "1.0.0-test",
                selectedProtocolVersion,
                new RuntimeCapabilities(),
                new RuntimeLimits(),
                $"memory://{RuntimeInstanceId}/events",
                15));
    }

    private async ValueTask<FakeControlResponse> HandleStartNewSessionRunAsync(
        FakeNewRunStart start)
    {
        if (_runsByIdempotencyKey.TryGetValue(
                start.Request.RunIdempotencyKey,
                out var existingRun))
        {
            return Success(existingRun.ToStartResult(isDuplicate: true));
        }

        if (!_sessionsByIdempotencyKey.TryGetValue(
                start.Request.SessionIdempotencyKey,
                out var session))
        {
            session = new FakeSession(
                $"session-{++_nextSessionId}",
                start.Request.SessionIdempotencyKey,
                start.Request.Mode,
                start.Request.Selection);
            _sessionsById.Add(session.SessionId, session);
            _sessionsByIdempotencyKey.Add(session.IdempotencyKey, session);
        }

        var run = CreateRun(session, start.Request.RunIdempotencyKey);
        await PublishRunClosureAsync(run, session.Selection, start.DuplicateDelivery);

        return Success(run.ToStartResult(isDuplicate: false));
    }

    private async ValueTask<FakeControlResponse> HandleStartExistingSessionRunAsync(
        FakeExistingRunStart start)
    {
        if (_runsByIdempotencyKey.TryGetValue(
                start.Request.RunIdempotencyKey,
                out var existingRun))
        {
            return Success(existingRun.ToStartResult(isDuplicate: true));
        }

        if (!_sessionsById.TryGetValue(start.Request.SessionId, out var session))
        {
            return Failure(
                RuntimeErrorCodes.SessionNotFound,
                $"Session '{start.Request.SessionId}' was not found.");
        }

        var selection = start.Request.TurnOverride ?? session.Selection;
        var run = CreateRun(session, start.Request.RunIdempotencyKey);
        await PublishRunClosureAsync(run, selection, start.DuplicateDelivery);

        return Success(run.ToStartResult(isDuplicate: false));
    }

    private FakeControlResponse HandleAcknowledge(EventAcknowledgeParameters request)
    {
        if (request.LastConfirmedGsn < LastConfirmedGsn ||
            request.LastConfirmedGsn > _nextGsn)
        {
            return Failure(
                "InvalidAcknowledgement",
                "The acknowledgement cursor is outside the valid range.");
        }

        var releasedGsns = _eventBuffer.Keys
            .Where(gsn => gsn <= request.LastConfirmedGsn)
            .ToArray();

        foreach (var gsn in releasedGsns)
        {
            _eventBuffer.Remove(gsn);
        }

        LastConfirmedGsn = request.LastConfirmedGsn;
        return Success(
            new FakeAcknowledgeResult(LastConfirmedGsn, releasedGsns.Length));
    }

    private FakeControlResponse HandleQuery(RunCancelParameters request)
    {
        return _runsById.TryGetValue(request.RunId, out var run)
            ? Success(run.ToState())
            : Failure(RuntimeErrorCodes.SessionNotFound, "The requested Run was not found.");
    }

    private FakeControlResponse HandleCancel(RunCancelParameters request)
    {
        if (!_runsById.TryGetValue(request.RunId, out var run))
        {
            return Failure(RuntimeErrorCodes.SessionNotFound, "The requested Run was not found.");
        }

        if (!IsTerminal(run.Status))
        {
            run.Status = RunStatus.Cancelled;
            run.TerminalEventCount++;
        }

        return Success(run.ToState());
    }

    private FakeControlResponse HandleSelectionUpdate(FakeSelectionUpdate update)
    {
        if (!_sessionsById.TryGetValue(update.SessionId, out var session))
        {
            return Failure(RuntimeErrorCodes.SessionNotFound, "The Session was not found.");
        }

        if (session.Selection.SelectionVersion != update.ExpectedSelectionVersion)
        {
            return Failure(
                RuntimeErrorCodes.SelectionConflict,
                "The expected selection version does not match the current version.");
        }

        session.Selection = update.Selection;
        return Success(session.Selection);
    }

    private FakeControlResponse HandleRehydrate(FakeRehydrateRequest request)
    {
        if (!_sessionsById.TryGetValue(request.SessionId, out var session))
        {
            return Failure(RuntimeErrorCodes.SessionNotFound, "The Session was not found.");
        }

        var agent = GetAgents(session.Selection)
            .SingleOrDefault(candidate => string.Equals(
                candidate.AgentId,
                request.AgentId,
                StringComparison.Ordinal));

        if (agent is null || !string.Equals(
                ComputePromptHash(agent.SystemPrompt),
                request.PromptHash,
                StringComparison.Ordinal))
        {
            return Failure(
                RuntimeErrorCodes.RehydrateHashMismatch,
                "The supplied Agent definition does not match the expected prompt hash.");
        }

        return Success(agent);
    }

    private FakeRun CreateRun(FakeSession session, string runIdempotencyKey)
    {
        var run = new FakeRun(
            session.SessionId,
            $"run-{++_nextRunId}",
            runIdempotencyKey,
            session.Mode);
        _runsById.Add(run.RunId, run);
        _runsByIdempotencyKey.Add(run.IdempotencyKey, run);
        return run;
    }

    private async ValueTask PublishRunClosureAsync(
        FakeRun run,
        NextTurnSelection selection,
        bool duplicateDelivery)
    {
        var agent = GetAgents(selection).First();
        var invocationId = $"invocation-{run.RunId}";
        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            agent.ProviderId ?? selection.DefaultSelection.ProviderId,
            agent.ModelId ?? selection.DefaultSelection.ModelId,
            ComputePromptHash(agent.SystemPrompt),
            null,
            null,
            selection.ToolCatalogVersion,
            "projection-v1",
            BaseTimestamp.AddMinutes(_nextRunId));

        var selectionUpdate = SelectionUpdateAfterSnapshotCreated?.Invoke(
            run.SessionId,
            snapshot);
        if (selectionUpdate is not null)
        {
            var updateResponse = HandleSelectionUpdate(selectionUpdate);
            if (updateResponse.Error is not null)
            {
                throw new InvalidOperationException(
                    $"The injected Selection update failed: {updateResponse.Error.Code}.");
            }
        }

        var eventSpecifications = new FakeEventSpecification[]
        {
            new(
                MessageTypes.RunAccepted,
                Serialize(
                    new RunAcceptedEvent(run.SessionId, run.RunId),
                    RuntimeJsonContext.Default.RunAcceptedEvent)),
            new(
                MessageTypes.RunStatusChanged,
                Serialize(
                    new RunStatusChangedEvent(run.RunId, RunStatus.Running.ToString()),
                    RuntimeJsonContext.Default.RunStatusChangedEvent)),
            new(
                MessageTypes.InvocationStarted,
                Serialize(
                    new InvocationStartedEvent(invocationId, snapshot),
                    RuntimeJsonContext.Default.InvocationStartedEvent)),
            new(
                MessageTypes.TextDelta,
                Serialize(
                    new TextDeltaEvent(invocationId, $"{run.Mode} delta"),
                    RuntimeJsonContext.Default.TextDeltaEvent)),
            new(
                MessageTypes.RunStatusChanged,
                Serialize(
                    new RunStatusChangedEvent(
                        run.RunId,
                        RunStatus.WaitingForTool.ToString()),
                    RuntimeJsonContext.Default.RunStatusChangedEvent)),
            new(
                MessageTypes.RunStatusChanged,
                Serialize(
                    new RunStatusChangedEvent(run.RunId, RunStatus.Running.ToString()),
                    RuntimeJsonContext.Default.RunStatusChangedEvent)),
            new(
                MessageTypes.InvocationCompleted,
                Serialize(
                    new InvocationCompletedEvent(invocationId),
                    RuntimeJsonContext.Default.InvocationCompletedEvent)),
            new(
                MessageTypes.RunStatusChanged,
                Serialize(
                    new RunStatusChangedEvent(run.RunId, RunStatus.Persisting.ToString()),
                    RuntimeJsonContext.Default.RunStatusChangedEvent)),
            new(
                MessageTypes.RunCompleted,
                Serialize(
                    new RunCompletedEvent(run.RunId, run.SessionId),
                    RuntimeJsonContext.Default.RunCompletedEvent))
        };

        run.FirstGsn = _nextGsn + 1;
        long runSequence = 0;

        foreach (var specification in eventSpecifications)
        {
            var runtimeEvent = new RuntimeEventEnvelope(
                RuntimeInstanceId,
                ++_nextGsn,
                run.RunId,
                ++runSequence,
                specification.MessageType,
                BaseTimestamp.AddSeconds(_nextGsn),
                specification.Payload);
            _eventBuffer.Add(runtimeEvent.Gsn, runtimeEvent);
            await _eventChannel.Writer.WriteAsync(runtimeEvent);
        }

        run.LastGsn = _nextGsn;
        run.Status = RunStatus.Completed;
        run.TerminalEventCount = 1;

        if (duplicateDelivery)
        {
            foreach (var runtimeEvent in _eventBuffer.Values.Where(runtimeEvent =>
                         runtimeEvent.Gsn >= run.FirstGsn &&
                         runtimeEvent.Gsn <= run.LastGsn))
            {
                await _eventChannel.Writer.WriteAsync(runtimeEvent);
            }
        }
    }

    private FakeRun GetRun(string runId)
    {
        return _runsById.TryGetValue(runId, out var run)
            ? run
            : throw new InvalidOperationException($"Run '{runId}' was not found.");
    }

    private static AgentRef[] GetAgents(NextTurnSelection selection)
    {
        return selection.ModeOptions switch
        {
            ExpertModeOptions expert => [expert.Agent],
            MeetingModeOptions meeting => meeting.Participants
                .Select(static participant => participant.Agent)
                .ToArray(),
            WorkModeOptions work => [work.GeneralManager, .. work.AvailableAgents],
            _ => throw new InvalidOperationException("The Runtime mode is not supported.")
        };
    }

    private static string ComputePromptHash(string prompt)
    {
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))
            .ToLowerInvariant();
    }

    private static JsonElement Serialize<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        return JsonSerializer.SerializeToElement(value, jsonTypeInfo);
    }

    private static T RequirePayload<T>(FakeControlRequest request)
        where T : class
    {
        return request.Payload as T ??
               throw new InvalidOperationException(
                   $"Operation {request.Operation} requires payload {typeof(T).Name}.");
    }

    private static FakeControlResponse Success(object value)
    {
        return new FakeControlResponse(value, null);
    }

    private static FakeControlResponse Failure(string code, string message)
    {
        return new FakeControlResponse(
            null,
            new RuntimeError(
                code,
                "Protocol",
                message,
                false,
                null,
                $"fake-runtime-{code}"));
    }

    private static bool IsTerminal(RunStatus status)
    {
        return status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
    }

    private sealed record FakeEventSpecification(
        string MessageType,
        JsonElement Payload);

    private sealed class FakeSession(
        string sessionId,
        string idempotencyKey,
        RuntimeMode mode,
        NextTurnSelection selection)
    {
        public string SessionId { get; } = sessionId;

        public string IdempotencyKey { get; } = idempotencyKey;

        public RuntimeMode Mode { get; } = mode;

        public NextTurnSelection Selection { get; set; } = selection;
    }

    private sealed class FakeRun(
        string sessionId,
        string runId,
        string idempotencyKey,
        RuntimeMode mode)
    {
        public string SessionId { get; } = sessionId;

        public string RunId { get; } = runId;

        public string IdempotencyKey { get; } = idempotencyKey;

        public RuntimeMode Mode { get; } = mode;

        public long FirstGsn { get; set; }

        public long LastGsn { get; set; }

        public RunStatus Status { get; set; } = RunStatus.Accepted;

        public int TerminalEventCount { get; set; }

        public FakeRunStartResult ToStartResult(bool isDuplicate)
        {
            return new FakeRunStartResult(
                SessionId,
                RunId,
                Mode,
                FirstGsn,
                LastGsn,
                isDuplicate);
        }

        public FakeRunState ToState()
        {
            return new FakeRunState(
                SessionId,
                RunId,
                Mode,
                Status,
                TerminalEventCount);
        }
    }
}
