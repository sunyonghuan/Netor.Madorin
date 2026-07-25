using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class RuntimeClientMultiInstanceIsolationStage7Tests
{
    private const string SharedRunId = "shared-run-id";
    private const string SharedCallId = "shared-call-id";
    private const long SharedGsn = 17;

    private readonly TestContext _testContext;

    public RuntimeClientMultiInstanceIsolationStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BoundClients_SameRunIdAndGsn_KeepAllOperationsInstanceScoped()
    {
        var callbacksA = new ConcurrentQueue<string>();
        var callbacksB = new ConcurrentQueue<string>();
        await using var runtimeA = new AuthenticatedFakeRuntime("a");
        await using var runtimeB = new AuthenticatedFakeRuntime("b");
        await using var clientA = await runtimeA.CreateClientAsync(
            CreateCallbacks(callbacksA),
            _testContext.CancellationToken);
        await using var clientB = await runtimeB.CreateClientAsync(
            CreateCallbacks(callbacksB),
            _testContext.CancellationToken);

        await Task.WhenAll(
            runtimeA.SendEventAsync(_testContext.CancellationToken),
            runtimeB.SendEventAsync(_testContext.CancellationToken));
        var eventA = await ReadOneEventAsync(clientA, _testContext.CancellationToken);
        var eventB = await ReadOneEventAsync(clientB, _testContext.CancellationToken);

        Assert.AreEqual(SharedRunId, eventA.RunId);
        Assert.AreEqual(SharedRunId, eventB.RunId);
        Assert.AreEqual(SharedGsn, eventA.Gsn);
        Assert.AreEqual(SharedGsn, eventB.Gsn);
        Assert.AreEqual(runtimeA.RuntimeInstanceId, eventA.RuntimeInstanceId);
        Assert.AreEqual(runtimeB.RuntimeInstanceId, eventB.RuntimeInstanceId);
        Assert.AreEqual("a", eventA.Payload.GetString());
        Assert.AreEqual("b", eventB.Payload.GetString());

        var queryA = await clientA.QueryRunAsync(
            SharedRunId,
            _testContext.CancellationToken);
        var queryB = await clientB.QueryRunAsync(
            SharedRunId,
            _testContext.CancellationToken);
        Assert.AreEqual("session-a", queryA.SessionId);
        Assert.AreEqual("session-b", queryB.SessionId);

        Assert.IsTrue(await clientA.CancelRunAsync(
            SharedRunId,
            _testContext.CancellationToken));
        Assert.AreEqual(1, runtimeA.CancelCount);
        Assert.AreEqual(0, runtimeB.CancelCount);
        Assert.IsTrue(await clientB.CancelRunAsync(
            SharedRunId,
            _testContext.CancellationToken));
        Assert.AreEqual(1, runtimeB.CancelCount);

        await clientA.AcknowledgeEventsAsync(SharedGsn, _testContext.CancellationToken);
        Assert.AreEqual(SharedGsn, runtimeA.LastAcknowledgedGsn);
        Assert.AreEqual(0L, runtimeB.LastAcknowledgedGsn);
        await clientB.AcknowledgeEventsAsync(SharedGsn, _testContext.CancellationToken);
        Assert.AreEqual(SharedGsn, runtimeB.LastAcknowledgedGsn);

        var responseA = await runtimeA.RequestPermissionAsync(
            SharedRunId,
            SharedCallId,
            _testContext.CancellationToken);
        var responseB = await runtimeB.RequestPermissionAsync(
            SharedRunId,
            SharedCallId,
            _testContext.CancellationToken);
        Assert.AreEqual("tool-a", responseA.Reason);
        Assert.AreEqual("tool-b", responseB.Reason);
        Assert.AreEqual("tool-a", Assert.ContainsSingle(callbacksA));
        Assert.AreEqual("tool-b", Assert.ContainsSingle(callbacksB));

        await clientA.DisposeAsync();
        var survivingQuery = await clientB.QueryRunAsync(
            SharedRunId,
            _testContext.CancellationToken);
        Assert.AreEqual("session-b", survivingQuery.SessionId);
        var survivingCallback = await runtimeB.RequestPermissionAsync(
            SharedRunId,
            SharedCallId,
            _testContext.CancellationToken);
        Assert.AreEqual("tool-b", survivingCallback.Reason);
        Assert.HasCount(2, callbacksB);
    }

    private static RuntimeHostCallbacks CreateCallbacks(ConcurrentQueue<string> calls) =>
        new()
        {
            MaxConcurrency = 1,
            CallbackTimeout = TimeSpan.FromSeconds(5),
            ToolPermissionRequestedAsync = (request, _) =>
            {
                calls.Enqueue(request.ToolId);
                return ValueTask.FromResult(new ToolPermissionResponse(
                    request.CorrelationId ?? request.ApprovalRequestId,
                    request.CallId,
                    ToolAuthorizationDecision.Granted,
                    Reason: request.ToolId));
            }
        };

    private static async Task<RuntimeEventEnvelope> ReadOneEventAsync(
        RuntimeClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in client.ReadEventsAsync(
            eventReplayCursor: 0,
            cancellationToken))
        {
            return envelope;
        }

        throw new EndOfStreamException("The fake Runtime closed without sending its event.");
    }

    private sealed class AuthenticatedFakeRuntime : IAsyncDisposable
    {
        private readonly string _name;
        private readonly string _controlPipeName;
        private readonly string _eventPipeName;
        private readonly string _hostInstanceId;
        private readonly string _secret;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<string> _sessionKey = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<FramedControlChannel> _control = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<FramedEventChannel> _events = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _controlTask;
        private readonly Task _eventTask;
        private long _lastAcknowledgedGsn;
        private int _cancelCount;

        public AuthenticatedFakeRuntime(string name)
        {
            _name = name;
            RuntimeInstanceId = $"runtime-{name}-{Guid.NewGuid():N}";
            _hostInstanceId = $"host-{name}-{Guid.NewGuid():N}";
            _controlPipeName = $"madorin.stage7.isolation.{name}.{Guid.NewGuid():N}";
            _eventPipeName = $"{_controlPipeName}.events";
            _secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _controlTask = RunControlServerAsync(_lifetime.Token);
            _eventTask = RunEventServerAsync(_lifetime.Token);
        }

        public string RuntimeInstanceId { get; }

        public int CancelCount => Volatile.Read(ref _cancelCount);

        public long LastAcknowledgedGsn => Interlocked.Read(ref _lastAcknowledgedGsn);

        public async ValueTask<RuntimeClient> CreateClientAsync(
            RuntimeHostCallbacks callbacks,
            CancellationToken cancellationToken) =>
            await RuntimeClient.CreateAsync(
                new RuntimeClientOptions(
                    _controlPipeName,
                    _hostInstanceId,
                    ConnectTimeout: TimeSpan.FromSeconds(5),
                    EventPipeName: _eventPipeName,
                    ExpectedRuntimeInstanceId: RuntimeInstanceId,
                    HandshakeSecret: _secret,
                    EnableBackgroundHeartbeat: false,
                    StartPolicy: RuntimeStartPolicy.AttachExisting,
                    HostCallbacks: callbacks),
                cancellationToken);

        public async Task SendEventAsync(CancellationToken cancellationToken)
        {
            var channel = await _events.Task.WaitAsync(cancellationToken);
            await channel.SendAsync(
                new RuntimeEventEnvelope(
                    RuntimeInstanceId,
                    SharedGsn,
                    SharedRunId,
                    RunSequence: 1,
                    "test.instance.event",
                    DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(
                        _name,
                        RuntimeJsonContext.Default.String)),
                cancellationToken);
        }

        public async Task<ToolPermissionResponse> RequestPermissionAsync(
            string runId,
            string callId,
            CancellationToken cancellationToken)
        {
            var channel = await _control.Task.WaitAsync(cancellationToken);
            var correlationId = $"correlation-{_name}";
            var request = new ToolPermissionRequest(
                $"approval-{_name}",
                $"agent-{_name}",
                ParentAgentId: null,
                $"tool-{_name}",
                callId,
                "arguments-hash",
                "Low",
                "workspace",
                "isolation test",
                correlationId,
                runId);
            var parameters = JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            var response = await channel.SendRequestAsync(
                MessageTypes.ToolPermissionRequest,
                parameters,
                cancellationToken);
            Assert.IsNull(response.Error, response.Error?.Message);
            Assert.IsNotNull(response.Result);
            return JsonSerializer.Deserialize(
                response.Result.Value,
                RuntimeJsonContext.Default.ToolPermissionResponse)
                ?? throw new InvalidDataException("The host callback response was empty.");
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            await ObserveServerTaskAsync(_controlTask);
            await ObserveServerTaskAsync(_eventTask);
            _lifetime.Dispose();
        }

        private async Task RunControlServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var transport = await NamedPipeTransport.CreateServerAsync(
                    _controlPipeName,
                    cancellationToken);
                var handshake = new FramedChannel(transport);
                var clientHello = await handshake.ReceiveJsonAsync(
                    RuntimeJsonContext.Default.HandshakeClientHello,
                    cancellationToken)
                    ?? throw new EndOfStreamException(
                        "The Client closed during the fake Runtime handshake.");
                if (!string.Equals(
                    _hostInstanceId,
                    clientHello.HostInstanceId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The Client used the wrong host instance.");
                }

                var nonceC = Convert.FromBase64String(clientHello.NonceC);
                var nonceS = HandshakeProtocol.CreateNonce();
                await handshake.SendJsonAsync(
                    new HandshakeServerHello(
                        RuntimeInstanceId,
                        Convert.ToBase64String(nonceS),
                        HandshakeProtocol.ComputeRuntimeResponse(
                            _secret,
                            RuntimeInstanceId,
                            clientHello.TimestampUnixMilliseconds,
                            nonceC,
                            nonceS)),
                    cancellationToken);
                var confirmation = await handshake.ReceiveJsonAsync(
                    RuntimeJsonContext.Default.HandshakeClientConfirmation,
                    cancellationToken)
                    ?? throw new EndOfStreamException(
                        "The Client closed before confirming the fake Runtime handshake.");
                var sessionKey = HandshakeProtocol.DeriveSessionKey(
                    _secret,
                    _hostInstanceId,
                    RuntimeInstanceId,
                    nonceC,
                    nonceS);
                if (!string.Equals(
                        _hostInstanceId,
                        confirmation.HostInstanceId,
                        StringComparison.Ordinal)
                    || !HandshakeProtocol.VerifySessionProof(
                        sessionKey,
                        "control-channel",
                        _hostInstanceId,
                        RuntimeInstanceId,
                        confirmation.Proof))
                {
                    throw new InvalidDataException("The Client confirmation proof was invalid.");
                }

                _sessionKey.TrySetResult(sessionKey);
                using var authenticated = new AuthenticatedFrameChannel(
                    transport,
                    sessionKey,
                    "runtime-control",
                    "host-control");
                await using var control = new FramedControlChannel(authenticated);
                control.SetRequestHandler(HandleRequestAsync);
                _control.TrySetResult(control);
                await control.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _sessionKey.TrySetException(ex);
                _control.TrySetException(ex);
                throw;
            }
        }

        private async Task RunEventServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var transport = await NamedPipeTransport.CreateServerAsync(
                    _eventPipeName,
                    NamedPipeTransport.MaxEventMessageBytes,
                    cancellationToken);
                var handshake = new FramedChannel(transport);
                var hello = await handshake.ReceiveJsonAsync(
                    RuntimeJsonContext.Default.EventChannelHello,
                    cancellationToken)
                    ?? throw new EndOfStreamException(
                        "The Client closed during fake event-channel authentication.");
                var sessionKey = await _sessionKey.Task.WaitAsync(cancellationToken);
                if (!string.Equals(_hostInstanceId, hello.HostInstanceId, StringComparison.Ordinal)
                    || !HandshakeProtocol.VerifySessionProof(
                        sessionKey,
                        "event-channel",
                        _hostInstanceId,
                        RuntimeInstanceId,
                        hello.Proof))
                {
                    throw new InvalidDataException("The event channel proof was invalid.");
                }

                await using var events = new FramedEventChannel(transport);
                _events.TrySetResult(events);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await transport.ReceiveFrameAsync(cancellationToken);
                    if (frame.Length == 0)
                    {
                        break;
                    }

                    throw new InvalidDataException("The fake event channel is send-only.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _events.TrySetException(ex);
                throw;
            }
        }

        private ValueTask<JsonRpcResponse> HandleRequestAsync(
            JsonRpcRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request.Method switch
            {
                "initialize" => ValueTask.FromResult(Success(
                    request,
                    new InitializeResponse(
                        RuntimeInstanceId,
                        "1.0.0",
                        ProtocolVersions.Current,
                        new RuntimeCapabilities(ReverseRpc: true),
                        new RuntimeLimits(),
                        _eventPipeName,
                        HeartbeatIntervalSeconds: 30),
                    RuntimeJsonContext.Default.InitializeResponse)),
                MessageTypes.RunQuery => ValueTask.FromResult(Success(
                    request,
                    new RunQueryResult(
                        SharedRunId,
                        $"session-{_name}",
                        RunStatus.Running,
                        TerminalText: null),
                    RuntimeJsonContext.Default.RunQueryResult)),
                MessageTypes.RunCancel => ValueTask.FromResult(HandleCancel(request)),
                "events.acknowledge" => ValueTask.FromResult(HandleAcknowledge(request)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unexpected method '{request.Method}'.")))
            };
        }

        private JsonRpcResponse HandleCancel(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _cancelCount);
            return Success(request, true, RuntimeJsonContext.Default.Boolean);
        }

        private JsonRpcResponse HandleAcknowledge(JsonRpcRequest request)
        {
            var parameters = request.Params is { } value
                ? JsonSerializer.Deserialize(
                    value,
                    RuntimeJsonContext.Default.EventAcknowledgeParameters)
                : null;
            if (parameters is null)
            {
                return new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32602, "The acknowledgement payload was missing."));
            }

            Interlocked.Exchange(ref _lastAcknowledgedGsn, parameters.LastConfirmedGsn);
            return Success(request, true, RuntimeJsonContext.Default.Boolean);
        }

        private static JsonRpcResponse Success<T>(
            JsonRpcRequest request,
            T result,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(
                "2.0",
                request.Id,
                JsonSerializer.SerializeToElement(result, typeInfo));

        private static async Task ObserveServerTaskAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex) when (ex is IOException
                or EndOfStreamException
                or ObjectDisposedException
                or OperationCanceledException)
            {
            }
        }
    }
}
