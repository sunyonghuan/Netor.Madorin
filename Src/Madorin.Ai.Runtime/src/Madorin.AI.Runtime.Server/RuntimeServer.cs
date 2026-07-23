using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.Auth;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Transport.Abstractions;
using Madorin.AI.Runtime.Transport.NamedPipes;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

public sealed class RuntimeServer : IAsyncDisposable
{
    private const string MeetingHitlTimeoutReason = "HITL approval timed out";

    private readonly WorkspaceWriteLock _instanceLock;
    private readonly FileStream _instanceNameLock;
    private readonly string _dataDirectory;
    private readonly string _databaseConnectionString;
    private readonly string _handshakeSecret;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _handshakeClockSkew;
    private readonly int _heartbeatIntervalSeconds;
    private readonly TimeSpan _hostLeaseTimeout;
    private readonly RuntimeLimits _limits;
    private readonly Func<NextTurnSelection, IRuntimeProviderAdapter>? _providerResolver;
    private readonly RuntimeExecutionCore _executionCore;
    private readonly BlobStagingStore _blobStore;
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunRequest> _activeRunRequests =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NextTurnSelection> _sessionSelections =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthenticatedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _seenClientNonces = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _eventDispatchGate = new(1, 1);
    private readonly SemaphoreSlim _meetingResumeGate = new(1, 1);
    private readonly RunRegistry _runRegistry;
    private readonly CancellationTokenSource _shutdown = new();
    private int _connectionId;
    private int _disposed;

    private RuntimeServer(
        RuntimeServerOptions options,
        WorkspaceWriteLock instanceLock,
        FileStream instanceNameLock,
        string databaseConnectionString)
    {
        _instanceLock = instanceLock;
        _instanceNameLock = instanceNameLock;
        _dataDirectory = options.DataDirectory;
        _databaseConnectionString = databaseConnectionString;
        _handshakeSecret = options.HandshakeSecret
            ?? throw new ArgumentException("A handshake secret is required.", nameof(options));
        _timeProvider = options.TimeProvider;
        _handshakeClockSkew = options.HandshakeClockSkew;
        _heartbeatIntervalSeconds = options.HeartbeatIntervalSeconds;
        _hostLeaseTimeout = options.HostLeaseTimeout;
        _limits = options.BlobLimits;
        _providerResolver = options.ProviderResolver;
        var userHome = options.MemoryUserHome
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        var memoryFiles = new MemoryFileService(
            Path.GetFullPath(userHome),
            options.WorkspaceRoot);
        var agentContextComposer = new AgentContextComposer(memoryFiles);
        _blobStore = new BlobStagingStore(
            _dataDirectory,
            options.BlobLimits,
            options.MaxGlobalBlobBytes);
        _executionCore = new RuntimeExecutionCore(
            _dataDirectory,
            databaseConnectionString,
            options.InstanceId,
            options.WorkspaceRoot,
            agentContextComposer,
            memoryFiles,
            CreateNegotiatedLimits(supportsStage5: true),
            _blobStore);
        _runRegistry = new RunRegistry(
            options.MaxConcurrentRuns,
            options.MaxConcurrentRunsPerProvider,
            options.MaxConcurrentRunsPerSession,
            options.RunTimeout,
            _shutdown.Token);
        InstanceId = options.InstanceId;
        WorkspaceRoot = options.WorkspaceRoot;
        PipeName = $"{options.PipePrefix}.{InstanceId[..Math.Min(8, InstanceId.Length)]}";
        EventPipeName = $"{PipeName}.events";
    }

    public string InstanceId { get; }

    public string WorkspaceRoot { get; }

    public string PipeName { get; }

    public string EventPipeName { get; }

    public int ConnectedSessionCount => _sessions.Count;

    public int ConnectedEventChannelCount => _sessions.Values.Count(
        static session => session.TryGetEventChannel() is not null);

    public async Task DisconnectControlChannelsAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            await session.DisconnectControlChannelAsync().ConfigureAwait(false);
        }
    }

    public async Task DisconnectEventChannelsAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            var eventChannel = session.TryGetEventChannel();
            if (eventChannel is not null)
            {
                session.DetachEventChannel(eventChannel);
                await eventChannel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task<RuntimeServer> StartAsync(
        RuntimeServerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRuns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRunsPerProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRunsPerSession);
        if (options.RunTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RunTimeout,
                "The Run timeout must be positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HeartbeatIntervalSeconds);
        if (options.HostLeaseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.HostLeaseTimeout,
                "The host lease timeout must be positive.");
        }
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (options.HandshakeClockSkew <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.HandshakeClockSkew,
                "Handshake clock skew must be positive.");
        }

        ct.ThrowIfCancellationRequested();
        var handshakeSecret = options.HandshakeSecret
            ?? throw new ArgumentException(
                "A handshake secret must be supplied through a controlled handle.",
                nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeSecret);
        var workspace = Path.GetFullPath(options.WorkspaceRoot);
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        var configDirectory = Path.GetFullPath(options.ConfigDirectory);
        var logDirectory = Path.GetFullPath(options.LogDirectory);
        ValidateDirectoryLayout(dataDirectory, configDirectory, logDirectory);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(logDirectory);
        var resolvedInstanceId = string.IsNullOrWhiteSpace(options.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : options.InstanceId;
        var resolvedPipePrefix = string.IsNullOrWhiteSpace(options.PipePrefix)
            ? "madorin.ai.runtime"
            : options.PipePrefix;
        var instanceNameLocksDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin.ai.runtime",
            "instance-locks");
        Directory.CreateDirectory(instanceNameLocksDirectory);
        var instanceLockName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{resolvedPipePrefix}\0{resolvedInstanceId}")))
            .ToLowerInvariant();
        FileStream instanceNameLock;
        try
        {
            instanceNameLock = new FileStream(
                Path.Combine(instanceNameLocksDirectory, instanceLockName + ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "The Runtime instance name or IPC endpoint is already active.",
                ex);
        }

        WorkspaceWriteLock instanceLock;
        try
        {
            instanceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspace,
                    resolvedInstanceId,
                    waitTimeout: TimeSpan.Zero,
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await instanceNameLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var normalized = options with
        {
            WorkspaceRoot = workspace,
            DataDirectory = dataDirectory,
            ConfigDirectory = configDirectory,
            LogDirectory = logDirectory,
            InstanceId = resolvedInstanceId,
            PipePrefix = resolvedPipePrefix,
            HandshakeSecret = handshakeSecret
        };

        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct).ConfigureAwait(false);
            return new RuntimeServer(
                normalized,
                instanceLock,
                instanceNameLock,
                connection.ConnectionString);
        }
        catch
        {
            await instanceLock.DisposeAsync().ConfigureAwait(false);
            await instanceNameLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var stoppingToken = linkedCancellation.Token;
        var pidPath = Path.Combine(_dataDirectory, "runtime.pid");
        await WritePidFileAsync(pidPath, ct).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(
                AcceptConnectionsAsync(
                    PipeName,
                    NamedPipeTransport.MaxControlMessageBytes,
                    HandleControlConnectionAsync,
                    stoppingToken),
                AcceptConnectionsAsync(
                    EventPipeName,
                    NamedPipeTransport.MaxEventMessageBytes,
                    HandleEventConnectionAsync,
                    stoppingToken),
                MonitorHostLeasesAsync(stoppingToken),
                RecoverPersistedMeetingApprovalsAsync(stoppingToken))
                .ConfigureAwait(false);
            await DrainConnectionsAsync().ConfigureAwait(false);
        }
        finally
        {
            TryDeletePidFile(pidPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var run in _activeRuns.Values)
        {
            run.Cancel();
        }

        await DrainConnectionsAsync().ConfigureAwait(false);
        await DrainRunsAsync().ConfigureAwait(false);
        _sessions.Clear();
        await _blobStore.DisposeAsync().ConfigureAwait(false);
        await _instanceLock.DisposeAsync().ConfigureAwait(false);
        await _instanceNameLock.DisposeAsync().ConfigureAwait(false);
        _eventDispatchGate.Dispose();
        _meetingResumeGate.Dispose();
        _runRegistry.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptConnectionsAsync(
        string pipeName,
        int maxFrameBytes,
        Func<NamedPipeTransport, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var transport = await NamedPipeTransport.CreateServerAsync(
                    pipeName,
                    maxFrameBytes,
                    ct).ConfigureAwait(false);
                var task = handler(transport, ct);
                var connectionId = Interlocked.Increment(ref _connectionId);
                _connections[connectionId] = task;
                _ = task.ContinueWith(
                    static (completed, state) =>
                    {
                        var tuple = ((RuntimeServer Server, int Id))state!;
                        tuple.Server._connections.TryRemove(tuple.Id, out _);
                        _ = completed.Exception;
                    },
                    (this, connectionId),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleControlConnectionAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        await using (transport.ConfigureAwait(false))
        {
            AuthenticatedSession? session = null;
            var sessionRegistered = false;
            try
            {
                session = await AuthenticateControlChannelAsync(transport, ct).ConfigureAwait(false);
                if (!_sessions.TryAdd(session.HostInstanceId, session))
                {
                    throw new InvalidDataException("The host instance already has an authenticated connection.");
                }

                sessionRegistered = true;
                await using var repositoryConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                var repository = new SqliteSessionRepository(repositoryConnection);
                var meetingRepository = new SqliteMeetingRepository(repositoryConnection);
                await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                using var outbox = CreateEventOutbox(outboxConnection);
                using var toolStateStore = new SqliteToolIntentRepository(repositoryConnection);
                var authorizationService = new ToolAuthorizationService(
                    toolStateStore,
                    RuntimeToolPolicy.CreateRestricted(WorkspaceRoot),
                    _timeProvider);
                using var authenticated = new AuthenticatedFrameChannel(
                    transport,
                    session.SessionKey,
                    "runtime-control",
                    "host-control");
                await using var controlChannel = new FramedControlChannel(authenticated);
                session.AttachControlPeer(controlChannel);
                controlChannel.SetRequestHandler(async (request, requestCt) =>
                {
                    session.Touch(_timeProvider.GetUtcNow());
                    await session.EnterRequestAsync(requestCt).ConfigureAwait(false);
                    try
                    {
                        return await CreateControlResponseAsync(
                            request,
                            session,
                            repository,
                            meetingRepository,
                            outbox,
                            authorizationService,
                            toolStateStore,
                            requestCt).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.ExitRequest();
                    }
                });
                controlChannel.SetBlobRequestHandler(async (frame, requestCt) =>
                {
                    session.Touch(_timeProvider.GetUtcNow());
                    await session.EnterRequestAsync(requestCt).ConfigureAwait(false);
                    try
                    {
                        return await HandleBlobFrameAsync(
                            BlobFrameProtocol.Decode(frame),
                            session,
                            requestCt).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.ExitRequest();
                    }
                });
                await controlChannel.Completion.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                if (sessionRegistered && session is not null)
                {
                    _sessions.TryRemove(session.HostInstanceId, out _);
                    await _blobStore.AbortSessionAsync(
                        session.HostInstanceId,
                        CancellationToken.None).ConfigureAwait(false);
                    session.Dispose();
                }
            }
        }
    }

    private async Task HandleEventConnectionAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        await using (transport.ConfigureAwait(false))
        {
            AuthenticatedSession? session = null;
            FramedEventChannel? eventChannel = null;
            var attached = false;
            try
            {
                var framedChannel = new FramedChannel(transport);
                var hello = await framedChannel.ReceiveJsonAsync(
                    RuntimeJsonContext.Default.EventChannelHello,
                    ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The event channel closed during authentication.");
                if (!_sessions.TryGetValue(hello.HostInstanceId, out session)
                    || !session.IsInitialized
                    || !HandshakeProtocol.VerifySessionProof(
                        session.SessionKey,
                        "event-channel",
                        session.HostInstanceId,
                        InstanceId,
                        hello.Proof))
                {
                    throw new InvalidDataException("The event channel session proof is invalid.");
                }

                await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                using var outbox = CreateEventOutbox(outboxConnection);
                eventChannel = new FramedEventChannel(transport);
                await ReplayPendingEventsAsync(session, outbox, eventChannel, ct)
                    .ConfigureAwait(false);
                if (!session.TryAttachEventChannel(eventChannel))
                {
                    throw new InvalidDataException("The host instance already has an event channel.");
                }

                attached = true;
                while (!ct.IsCancellationRequested)
                {
                    var frame = await transport.ReceiveFrameAsync(ct).ConfigureAwait(false);
                    if (frame.Length == 0)
                    {
                        break;
                    }

                    throw new InvalidDataException("The event channel is Runtime-to-Host only.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                if (attached && session is not null && eventChannel is not null)
                {
                    session.DetachEventChannel(eventChannel);
                }
            }
        }
    }

    private async Task<AuthenticatedSession> AuthenticateControlChannelAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        var channel = new FramedChannel(transport);
        var hello = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientHello,
            ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The control channel closed during authentication.");
        ArgumentException.ThrowIfNullOrWhiteSpace(hello.HostInstanceId);
        var nonceC = DecodeNonce(hello.NonceC);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var skew = Math.Abs(now - hello.TimestampUnixMilliseconds);
        if (hello.TimestampUnixMilliseconds <= 0
            || skew > _handshakeClockSkew.TotalMilliseconds)
        {
            throw new InvalidDataException("The client handshake timestamp is outside the accepted window.");
        }

        var nonceKey = Convert.ToBase64String(nonceC);
        PruneSeenNonces(now);
        if (!_seenClientNonces.TryAdd(nonceKey, now))
        {
            throw new InvalidDataException("The client handshake nonce was replayed.");
        }

        var nonceS = HandshakeHandler.CreateNonce();
        var runtimeResponse = HandshakeHandler.ComputeRuntimeResponse(
            _handshakeSecret,
            InstanceId,
            hello.TimestampUnixMilliseconds,
            nonceC,
            nonceS);
        await channel.SendJsonAsync(
            new HandshakeServerHello(
                InstanceId,
                Convert.ToBase64String(nonceS),
                runtimeResponse),
            ct).ConfigureAwait(false);

        var result = HandshakeHandler.Complete(
            _handshakeSecret,
            hello.HostInstanceId,
            InstanceId,
            nonceC,
            nonceS);
        var confirmation = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientConfirmation,
            ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The control channel closed before host confirmation.");
        if (!string.Equals(confirmation.HostInstanceId, hello.HostInstanceId, StringComparison.Ordinal)
            || !HandshakeProtocol.VerifySessionProof(
                result.SessionKey,
                "control-channel",
                result.HostInstanceId,
                result.RuntimeInstanceId,
                confirmation.Proof))
        {
            throw new InvalidDataException("The host handshake confirmation is invalid.");
        }

        return new AuthenticatedSession(
            result.HostInstanceId,
            result.SessionKey,
            transport,
            _timeProvider.GetUtcNow());
    }

    private async Task RecoverSentToolIntentsAsync(
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteToolIntentRepository toolStateStore,
        SqliteEventOutbox outbox,
        ToolCatalogSnapshot toolCatalogSnapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(toolStateStore);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(toolCatalogSnapshot);

        var executors = new List<IToolExecutor>();
        if (session.SupportsReverseRpc)
        {
            executors.Add(new ReverseRpcToolExecutor(
                new ReconnectableDuplexRpcPeer(
                    () => ResolveReverseRpcPeer(session.HostInstanceId)),
                _blobStore));
        }

        using var toolGateway = new ToolGateway(
            session.ToolCatalogStore,
            new JsonSchemaToolValidator(),
            new ToolAuthorizationService(
                toolStateStore,
                RuntimeToolPolicy.CreateRestricted(WorkspaceRoot),
                _timeProvider),
            executors,
            toolStateStore,
            outbox,
            _timeProvider,
            _blobStore,
            _limits);
        var recoveryService = new RecoveryService(
            repository,
            toolStateStore,
            toolGateway,
            toolCatalogSnapshot,
            new SqliteWorkRepository(repository.Connection));
        await recoveryService.RecoverSentToolIntentsAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonRpcResponse> CreateControlResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteMeetingRepository meetingRepository,
        SqliteEventOutbox outbox,
        ToolAuthorizationService authorizationService,
        SqliteToolIntentRepository toolStateStore,
        CancellationToken ct)
    {
        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return Error(request.Id, -32600, "The JSON-RPC version must be 2.0.");
        }

        if (string.Equals(request.Method, "initialize", StringComparison.Ordinal))
        {
            var initializeRequest = request.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, RuntimeJsonContext.Default.InitializeRequest)
                : null;
            if (initializeRequest is null
                || !string.Equals(
                    initializeRequest.HostInstanceId,
                    session.HostInstanceId,
                    StringComparison.Ordinal)
                || initializeRequest.LastConfirmedGsn < 0)
            {
                return Error(request.Id, -32602, "The initialize request is invalid.");
            }

            var protocolVersion = ProtocolVersions.Negotiate(
                initializeRequest.SupportedProtocolVersions);
            if (protocolVersion is null)
            {
                return Error(request.Id, -32001, "No supported protocol version was offered.");
            }

            await outbox.AcknowledgeAsync(initializeRequest.LastConfirmedGsn, ct)
                .ConfigureAwait(false);
            var supportsStage5 = string.Equals(
                protocolVersion,
                ProtocolVersions.Current,
                StringComparison.Ordinal);
            session.Initialize(
                initializeRequest.LastConfirmedGsn,
                protocolVersion,
                initializeRequest.ExpectedCapabilities);
            var initializeResponse = new InitializeResponse(
                InstanceId,
                typeof(RuntimeServer).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                protocolVersion,
                new RuntimeCapabilities(
                    Streaming: false,
                    ToolCalling: supportsStage5,
                    BlobTransfer: true,
                    MultiRun: true,
                    SessionResume: true,
                    ReverseRpc: supportsStage5 ? true : null,
                    ToolCatalog: supportsStage5 ? true : null,
                    ToolPermissions: supportsStage5 ? true : null),
                CreateNegotiatedLimits(supportsStage5),
                EventPipeName,
                _heartbeatIntervalSeconds);
            var result = JsonSerializer.SerializeToElement(
                initializeResponse,
                RuntimeJsonContext.Default.InitializeResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (!session.IsInitialized)
        {
            return Error(request.Id, -32002, "The control channel has not been initialized.");
        }

        if (string.Equals(request.Method, MessageTypes.ToolCatalogReplace, StringComparison.Ordinal))
        {
            if (!session.SupportsToolCatalog)
            {
                return Error(request.Id, -32006, "Tool Catalog requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } catalogParameters
                ? JsonSerializer.Deserialize(
                    catalogParameters,
                    RuntimeJsonContext.Default.ToolCatalogReplaceRequest)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The tool.catalog.replace parameters are invalid.");
            }

            var snapshot = session.ToolCatalogStore.ReplaceHostCatalog(parameters);
            await RecoverSentToolIntentsAsync(
                session,
                repository,
                toolStateStore,
                outbox,
                snapshot,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new ToolCatalogUpdateResponse(
                    snapshot.HostCatalogVersion,
                    snapshot.EffectiveVersion,
                    snapshot.Tools.Count),
                RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.ToolCatalogPatch, StringComparison.Ordinal))
        {
            if (!session.SupportsToolCatalog)
            {
                return Error(request.Id, -32006, "Tool Catalog requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } catalogParameters
                ? JsonSerializer.Deserialize(
                    catalogParameters,
                    RuntimeJsonContext.Default.ToolCatalogPatchRequest)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The tool.catalog.patch parameters are invalid.");
            }

            var snapshot = session.ToolCatalogStore.PatchHostCatalog(parameters);
            await RecoverSentToolIntentsAsync(
                session,
                repository,
                toolStateStore,
                outbox,
                snapshot,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new ToolCatalogUpdateResponse(
                    snapshot.HostCatalogVersion,
                    snapshot.EffectiveVersion,
                    snapshot.Tools.Count),
                RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.GrantRevoke, StringComparison.Ordinal))
        {
            if (!session.SupportsToolPermissions)
            {
                return Error(request.Id, -32006, "Grant revocation requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } revokeParameters
                ? JsonSerializer.Deserialize(
                    revokeParameters,
                    RuntimeJsonContext.Default.GrantRevokeRequest)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.GrantId))
            {
                return Error(request.Id, -32602, "The grant.revoke parameters are invalid.");
            }

            var revokedCount = await authorizationService.RevokeGrantAsync(
                parameters.GrantId,
                parameters.RevokeDescendants,
                parameters.Reason,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new GrantRevokeResponse(parameters.GrantId, revokedCount),
                RuntimeJsonContext.Default.GrantRevokeResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.NewSessionRun, StringComparison.Ordinal))
        {
            return await CreateNewSessionRunResponseAsync(
                request,
                session,
                repository,
                outbox,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.ExistingSessionRun, StringComparison.Ordinal))
        {
            return await CreateExistingSessionRunResponseAsync(
                request,
                session,
                repository,
                outbox,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.RunQuery, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } queryParameters
                ? JsonSerializer.Deserialize(
                    queryParameters,
                    RuntimeJsonContext.Default.RunQueryParameters)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.RunId))
            {
                return Error(request.Id, -32602, "The run.query parameters are invalid.");
            }

            var snapshot = await repository.GetRunSnapshotAsync(parameters.RunId, ct)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return Error(request.Id, -32004, $"Run '{parameters.RunId}' was not found.");
            }

            var result = JsonSerializer.SerializeToElement(
                new RunQueryResult(
                    snapshot.RunId,
                    snapshot.SessionId,
                    snapshot.Status,
                    snapshot.TerminalText),
                RuntimeJsonContext.Default.RunQueryResult);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.SessionGet, StringComparison.Ordinal))
        {
            return await CreateSessionGetResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionResume, StringComparison.Ordinal))
        {
            return await CreateSessionResumeResponseAsync(request, repository, meetingRepository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionMessagesList, StringComparison.Ordinal))
        {
            return await CreateSessionMessagesResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionSelectionUpdate, StringComparison.Ordinal))
        {
            return await CreateSessionSelectionUpdateResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionRehydrate, StringComparison.Ordinal))
        {
            return await CreateSessionRehydrateResponseAsync(
                request,
                session,
                repository,
                meetingRepository,
                outbox,
                ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, "events.acknowledge", StringComparison.Ordinal))
        {
            if (request.Params is not { } acknowledgeParameters
                || acknowledgeParameters.ValueKind is not JsonValueKind.Object
                || !acknowledgeParameters.TryGetProperty(
                    "lastConfirmedGsn",
                    out var lastConfirmedProperty)
                || !lastConfirmedProperty.TryGetInt64(out var lastConfirmedGsn)
                || lastConfirmedGsn < 0)
            {
                return Error(request.Id, -32602, "The acknowledgement parameters are invalid.");
            }

            await outbox.AcknowledgeAsync(lastConfirmedGsn, ct).ConfigureAwait(false);
            session.Confirm(lastConfirmedGsn);
            var result = JsonSerializer.SerializeToElement(true, RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, "heartbeat", StringComparison.Ordinal))
        {
            var result = JsonSerializer.SerializeToElement(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                RuntimeJsonContext.Default.Int64);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.MeetingHitlResponse, StringComparison.Ordinal))
        {
            var response = request.Params is { } hitlParams
                ? JsonSerializer.Deserialize(
                    hitlParams,
                    RuntimeJsonContext.Default.MeetingHitlResponse)
                : null;
            if (response is null
                || string.IsNullOrWhiteSpace(response.ApprovalRequestId)
                || !Enum.IsDefined(response.Action))
            {
                return Error(request.Id, -32602, "The meeting.hitl.response parameters are invalid.");
            }

            if (response.Action == MeetingHitlAction.Supplement
                && string.IsNullOrWhiteSpace(response.Supplement))
            {
                return Error(request.Id, -32602, "Supplement action requires a nonblank Supplement.");
            }

            var approval = await meetingRepository.FindApprovalByRequestIdAsync(
                    response.ApprovalRequestId, ct)
                .ConfigureAwait(false);
            if (approval is null)
            {
                return Error(request.Id, -32004, "The approval request was not found.");
            }

            var isDuplicate = IsSameMeetingHitlResponse(approval.ApprovalJson, response);
            if (string.Equals(approval.Status, "Decided", StringComparison.Ordinal))
            {
                if (!isDuplicate)
                {
                    return Error(request.Id, -32009, "The approval request has already been decided.");
                }
            }

            if (!_activeRuns.TryGetValue(approval.RunId, out var hitlRun))
            {
                try
                {
                    await ResumePersistedMeetingHitlAsync(
                        approval,
                        response,
                        session,
                        meetingRepository,
                        outbox,
                        ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(request.Id, -32006, ex.Message);
                }

                var resumedResult = JsonSerializer.SerializeToElement(
                    true, RuntimeJsonContext.Default.Boolean);
                return new JsonRpcResponse("2.0", request.Id, resumedResult);
            }

            if (!string.Equals(
                    hitlRun.OwnerHostInstanceId,
                    session.HostInstanceId,
                    StringComparison.Ordinal))
            {
                return Error(request.Id, -32004, "The Run is not owned by this Host session.");
            }

            if (string.Equals(approval.Status, "Decided", StringComparison.Ordinal))
            {
                var duplicateResult = JsonSerializer.SerializeToElement(
                    true, RuntimeJsonContext.Default.Boolean);
                return new JsonRpcResponse("2.0", request.Id, duplicateResult);
            }

            if (!hitlRun.HasMeetingHitlWaiter(response.ApprovalRequestId))
            {
                return Error(request.Id, -32010, "No active waiter for this approval request.");
            }

            var responseJson = JsonSerializer.Serialize(
                response,
                RuntimeJsonContext.Default.MeetingHitlResponse);
            await meetingRepository.ResolvePendingApprovalAsync(
                approval.SessionId,
                response.ApprovalRequestId,
                responseJson,
                "Decided",
                ct).ConfigureAwait(false);

            var responseRunSequence = await outbox.GetNextRunSequenceAsync(
                approval.RunId,
                ct).ConfigureAwait(false);
            await outbox.AppendAsync(
                approval.RunId,
                responseRunSequence,
                MessageTypes.MeetingHitlResponse,
                responseJson,
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);

            if (!hitlRun.TryCompleteMeetingHitl(response))
            {
                return Error(request.Id, -32010, "No active waiter for this approval request.");
            }

            var hitlResult = JsonSerializer.SerializeToElement(
                true, RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, hitlResult);
        }

        if (string.Equals(request.Method, "run.cancel", StringComparison.Ordinal))
        {
            var parameters = request.Params is { } cancelParameters
                ? JsonSerializer.Deserialize(
                    cancelParameters,
                    RuntimeJsonContext.Default.RunCancelParameters)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.RunId))
            {
                return Error(request.Id, -32602, "The run.cancel parameters are invalid.");
            }

            var cancelAccepted = _activeRuns.TryGetValue(parameters.RunId, out var activeRun)
                && activeRun.Cancel();
            var result = JsonSerializer.SerializeToElement(
                cancelAccepted,
                RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.CredentialsUpdate, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } credentialParameters
                ? JsonSerializer.Deserialize(
                    credentialParameters,
                    RuntimeJsonContext.Default.CredentialsUpdateParameters)
                : null;
            if (parameters is null
                || string.IsNullOrWhiteSpace(parameters.RunId)
                || string.IsNullOrWhiteSpace(parameters.ProviderId)
                || string.IsNullOrWhiteSpace(parameters.Credential))
            {
                return Error(request.Id, -32602, "The credentials.update parameters are invalid.");
            }

            if (!_activeRuns.TryGetValue(parameters.RunId, out var credentialRun)
                || !string.Equals(
                    credentialRun.OwnerHostInstanceId,
                    session.HostInstanceId,
                    StringComparison.Ordinal))
            {
                return Error(request.Id, -32004, "The Run is not owned by this Host session.");
            }

            if (!await credentialRun.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false))
            {
                return Error(
                    request.Id,
                    -32005,
                    "The selected Provider cannot accept credential updates or is not waiting for credentials.");
            }

            var result = JsonSerializer.SerializeToElement(true, RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        return Error(request.Id, -32601, $"Unknown method '{request.Method}'.");
    }

    private async Task<byte[]> HandleBlobFrameAsync(
        BlobFrame frame,
        AuthenticatedSession session,
        CancellationToken ct)
    {
        try
        {
            switch (frame.Operation)
            {
                case BlobFrameOperation.Begin:
                    await _blobStore.BeginAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobBeginRequest),
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                case BlobFrameOperation.Chunk:
                    await _blobStore.AppendChunkAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobChunkRequest),
                        frame.Data,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                case BlobFrameOperation.Complete:
                    var completed = await _blobStore.CompleteAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobCompleteRequest),
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(
                        frame.Operation,
                        new BlobOperationResponse(true, null, completed, completed.Length, true),
                        frame: default);

                case BlobFrameOperation.Read:
                    var readRequest = BlobFrameProtocol.DeserializeMetadata(
                        frame,
                        RuntimeJsonContext.Default.BlobReadRequest);
                    if (readRequest.MaxBytes == 0)
                    {
                        var reference = await _blobStore.GetReferenceAsync(
                            readRequest,
                            session.HostInstanceId,
                            ct).ConfigureAwait(false);
                        return EncodeBlobResponse(
                            frame.Operation,
                            new BlobOperationResponse(true, null, reference, readRequest.Offset, false),
                            frame: default);
                    }

                    var chunk = await _blobStore.ReadChunkAsync(
                        readRequest,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(
                        frame.Operation,
                        new BlobOperationResponse(
                            true,
                            null,
                            chunk.Reference,
                            chunk.Offset,
                            chunk.EndOfBlob),
                        chunk.Data);

                case BlobFrameOperation.Abort:
                    var abort = BlobFrameProtocol.DeserializeMetadata(
                        frame,
                        RuntimeJsonContext.Default.BlobChunkRequest);
                    await _blobStore.AbortUploadAsync(
                        abort.UploadId,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                default:
                    throw new InvalidDataException("The Blob operation is not supported.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or FileNotFoundException
            or UnauthorizedAccessException)
        {
            return EncodeBlobResponse(
                frame.Operation,
                new BlobOperationResponse(false, ex.Message, null, 0, true),
                frame: default);
        }
    }

    private static byte[] EncodeBlobResponse(
        BlobFrameOperation operation,
        BlobOperationResponse? response,
        ReadOnlyMemory<byte> frame = default) =>
        BlobFrameProtocol.Encode(
            operation,
            response ?? new BlobOperationResponse(true, null, null, 0, true),
            RuntimeJsonContext.Default.BlobOperationResponse,
            frame,
            isResponse: true);

    private static async Task<JsonRpcResponse> CreateSessionGetResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionIdParameters)
            : null;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            return Error(request.Id, -32602, "The session.get parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = CreateRecoveryDiagnostics(snapshot);
        var selection = DeserializePersistedSelection(snapshot, diagnostics);
        var result = new SessionGetResult(
            snapshot.SessionId,
            snapshot.Mode,
            snapshot.Status,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            selection,
            CreateRunQueryResult(snapshot.LatestRun),
            IsFullyRecoverable: diagnostics.Count == 0,
            [.. diagnostics]);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionGetResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionResumeResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        SqliteMeetingRepository meetingRepository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionIdParameters)
            : null;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            return Error(request.Id, -32602, "The session.resume parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = CreateRecoveryDiagnostics(snapshot);
        var selection = DeserializePersistedSelection(snapshot, diagnostics);
        var neededDefinitions = _sessionSelections.ContainsKey(snapshot.SessionId)
            ? []
            : CreateNeededDefinitions(snapshot, selection);
        var historyMeta = await TryGetConversationHistoryMetadataAsync(snapshot.SessionId, ct)
            .ConfigureAwait(false);

        MeetingResumeState? meetingState = null;
        if (snapshot.Mode == RuntimeMode.Meeting)
        {
            var meetingSnapshot = await meetingRepository.GetMeetingSnapshotAsync(
                parameters.SessionId, ct).ConfigureAwait(false);
            if (meetingSnapshot is not null)
            {
                meetingState = BuildMeetingResumeState(
                    meetingSnapshot,
                    snapshot,
                    selection,
                    diagnostics);
            }
            else
            {
                diagnostics.Add(
                    "The Meeting session structure does not exist yet; meeting state is unavailable.");
            }
        }

        WorkResumeState? workState = null;
        if (snapshot.Mode == RuntimeMode.Work)
        {
            workState = await new SqliteWorkRepository(repository.Connection)
                .GetResumeStateAsync(parameters.SessionId, ct).ConfigureAwait(false);
            if (workState is null)
            {
                diagnostics.Add(
                    "The Work session structure does not exist yet; work state is unavailable.");
            }
        }

        var result = new SessionResumeResult(
            snapshot.SessionId,
            snapshot.Mode,
            snapshot.SelectionVersion,
            selection,
            neededDefinitions,
            CreateRunQueryResult(snapshot.LatestRun),
            snapshot.LastGsn,
            LastCheckpoint: null,
            IsFullyRecoverable: diagnostics.Count == 0,
            [.. diagnostics],
            historyMeta.MessageCount,
            historyMeta.LastMessageSeq,
            historyMeta.LastMessageId,
            snapshot.AgentSnapshots,
            historyMeta.BlobReferenceCount,
            historyMeta.BlobIds,
            meetingState,
            workState);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionResumeResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private static async Task<JsonRpcResponse> CreateSessionMessagesResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionMessagesListParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.PageSize is <= 0 or > 500
            || parameters.Cursor is < 0)
        {
            return Error(request.Id, -32602, "The session.messages.list parameters are invalid.");
        }

        IReadOnlyList<MessageIndexEntry> entries;
        try
        {
            entries = await repository.ListMessageIndexAsync(
                parameters.SessionId,
                parameters.Cursor,
                parameters.PageSize + 1,
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var hasMore = entries.Count > parameters.PageSize;
        var messages = entries.Take(parameters.PageSize)
            .Select(static entry => new SessionMessageDescriptor(
                entry.MessageId,
                entry.Sequence,
                entry.InvocationId,
                entry.AgentId,
                entry.Role,
                entry.CreatedAt))
            .ToArray();
        var result = new SessionMessagesListResult(
            messages,
            hasMore ? messages[^1].Sequence : null);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionMessagesListResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionSelectionUpdateResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionSelectionUpdateParameters)
            : null;
        var hasSelection = parameters?.Selection is not null;
        var hasPatches = parameters?.MeetingPatches is { Length: > 0 };
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.ExpectedSelectionVersion < 0
            || hasSelection == hasPatches
            || (hasSelection && parameters.Selection!.SelectionVersion <= parameters.ExpectedSelectionVersion))
        {
            return Error(request.Id, -32602, "The session.selection.update parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        if (snapshot.SelectionVersion is null || snapshot.SelectionJson is null)
        {
            return Error(
                request.Id,
                -32602,
                "The session has no persisted selection to patch.");
        }

        NextTurnSelection selection;
        if (hasSelection)
        {
            selection = parameters.Selection!;
            if (selection.Mode != snapshot.Mode)
            {
                return Error(request.Id, -32602, "A Session's mode cannot be changed.");
            }
        }
        else
        {
            if (snapshot.Mode != RuntimeMode.Meeting)
            {
                return Error(
                    request.Id,
                    -32602,
                    "Meeting patches are only supported for Meeting sessions.");
            }

            var diagnostics = new List<string>();
            var baseSelection = DeserializePersistedSelection(snapshot, diagnostics);
            if (baseSelection is null || baseSelection.ModeOptions is not MeetingModeOptions baseMeeting)
            {
                return Error(
                    request.Id,
                    -32602,
                    "The session has no valid Meeting selection to patch.");
            }

            var patchedParticipants = new List<MeetingParticipant>(baseMeeting.Participants);
            foreach (var patch in parameters.MeetingPatches!)
            {
                switch (patch)
                {
                    case AddParticipantPatch add:
                        if (patchedParticipants.Any(
                                p => string.Equals(p.ParticipantId, add.Participant.ParticipantId, StringComparison.Ordinal)))
                        {
                            return Error(
                                request.Id,
                                -32602,
                                $"Participant '{add.Participant.ParticipantId}' already exists.");
                        }
                        patchedParticipants.Add(add.Participant);
                        break;

                    case RemoveParticipantPatch remove:
                        var removeIdx = patchedParticipants.FindIndex(
                            p => string.Equals(p.ParticipantId, remove.ParticipantId, StringComparison.Ordinal));
                        if (removeIdx < 0)
                        {
                            return Error(
                                request.Id,
                                -32602,
                                $"Participant '{remove.ParticipantId}' was not found.");
                        }
                        patchedParticipants.RemoveAt(removeIdx);
                        break;

                    case UpdateParticipantPatch update:
                        var updateIdx = patchedParticipants.FindIndex(
                            p => string.Equals(p.ParticipantId, update.Participant.ParticipantId, StringComparison.Ordinal));
                        if (updateIdx < 0)
                        {
                            return Error(
                                request.Id,
                                -32602,
                                $"Participant '{update.Participant.ParticipantId}' was not found.");
                        }
                        patchedParticipants[updateIdx] = update.Participant;
                        break;

                    case UpdateParticipantStatusPatch updateStatus:
                        var statusIdx = patchedParticipants.FindIndex(
                            p => string.Equals(p.ParticipantId, updateStatus.ParticipantId, StringComparison.Ordinal));
                        if (statusIdx < 0)
                        {
                            return Error(
                                request.Id,
                                -32602,
                                $"Participant '{updateStatus.ParticipantId}' was not found.");
                        }
                        if (updateStatus.Status != ParticipantStatus.Active
                            && updateStatus.Status != ParticipantStatus.Standby)
                        {
                            return Error(
                                request.Id,
                                -32602,
                                "Participant status can only be Active or Standby.");
                        }
                        patchedParticipants[statusIdx] = patchedParticipants[statusIdx] with
                        {
                            Status = updateStatus.Status
                        };
                        break;

                    case ReorderParticipantsPatch reorder:
                        if (reorder.Orders.Length == 0)
                        {
                            return Error(
                                request.Id,
                                -32602,
                                "Reorder requires at least one entry.");
                        }
                        if (reorder.Orders.GroupBy(o => o.ParticipantId, StringComparer.Ordinal)
                                .Any(g => g.Count() > 1))
                        {
                            return Error(
                                request.Id,
                                -32602,
                                "Reorder entries must not contain duplicate participant IDs.");
                        }
                        if (reorder.Orders.GroupBy(o => o.JoinOrder).Any(g => g.Count() > 1))
                        {
                            return Error(
                                request.Id,
                                -32602,
                                "Reorder entries must not contain duplicate join orders.");
                        }
                        if (reorder.Orders.Any(o => o.JoinOrder < 0))
                        {
                            return Error(
                                request.Id,
                                -32602,
                                "Join order must not be negative.");
                        }
                        foreach (var order in reorder.Orders)
                        {
                            if (!patchedParticipants.Any(
                                    p => string.Equals(p.ParticipantId, order.ParticipantId, StringComparison.Ordinal)))
                            {
                                return Error(
                                    request.Id,
                                    -32602,
                                    $"Participant '{order.ParticipantId}' was not found.");
                            }
                        }
                        var reorderMap = reorder.Orders.ToDictionary(
                            o => o.ParticipantId,
                            o => o.JoinOrder,
                            StringComparer.Ordinal);
                        for (var i = 0; i < patchedParticipants.Count; i++)
                        {
                            if (reorderMap.TryGetValue(patchedParticipants[i].ParticipantId, out var newOrder))
                            {
                                patchedParticipants[i] = patchedParticipants[i] with { JoinOrder = newOrder };
                            }
                        }
                        break;
                }
            }

            if (patchedParticipants.Any(p => p.Status == ParticipantStatus.Removed))
            {
                return Error(
                    request.Id,
                    -32602,
                    "Participants with Removed status cannot be persisted; use the remove patch instead.");
            }

            var activeCount = patchedParticipants.Count(
                p => p.Status == ParticipantStatus.Active);
            if (patchedParticipants.Count < MeetingParticipant.MinCount
                || patchedParticipants.Count > MeetingParticipant.HardMaxCount)
            {
                return Error(
                    request.Id,
                    -32602,
                    $"A Meeting requires {MeetingParticipant.MinCount} to {MeetingParticipant.HardMaxCount} participants.");
            }

            if (activeCount < 1)
            {
                return Error(
                    request.Id,
                    -32602,
                    "At least one participant must be Active.");
            }

            if (patchedParticipants.Any(p => string.IsNullOrWhiteSpace(p.Agent.AgentId)))
            {
                return Error(
                    request.Id,
                    -32602,
                    "A participant AgentId must not be empty.");
            }

            if (patchedParticipants.GroupBy(p => p.ParticipantId, StringComparer.Ordinal)
                    .Any(g => g.Count() > 1))
            {
                return Error(
                    request.Id,
                    -32602,
                    "Participant IDs must be unique.");
            }

            if (patchedParticipants.GroupBy(p => p.JoinOrder).Any(g => g.Count() > 1))
            {
                return Error(
                    request.Id,
                    -32602,
                    "Join orders must be unique.");
            }

            var newParticipants = patchedParticipants
                .OrderBy(p => p.JoinOrder)
                .ToArray();
            var newMeeting = baseMeeting with { Participants = newParticipants };
            selection = baseSelection with
            {
                SelectionVersion = parameters.ExpectedSelectionVersion + 1,
                ModeOptions = newMeeting
            };
        }

        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(selection);
        var newSelectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var agentSnapshots = RuntimeRunMetadata.CreateAgentSnapshots(selection);

        bool saved;
        if (selection.ModeOptions is MeetingModeOptions meetingOptions)
        {
            var meetingInputs = meetingOptions.Participants
                .Select(p => new MeetingParticipantInput(
                    p.ParticipantId,
                    p.Agent.AgentId,
                    JsonSerializer.Serialize(p.Agent, RuntimeJsonContext.Default.AgentRef),
                    p.DisplayName,
                    p.JoinOrder,
                    p.Status == ParticipantStatus.Removed ? "Removed" : p.Status.ToString().ToLowerInvariant()))
                .ToArray();
            var policyJson = JsonSerializer.Serialize(
                meetingOptions.EffectivePolicy,
                RuntimeJsonContext.Default.MeetingPolicy);
            var policyHash = RuntimeRunMetadata.ComputePromptHash(policyJson);
            saved = await repository.TrySaveSessionSelectionWithMeetingAsync(
                parameters.SessionId,
                parameters.ExpectedSelectionVersion,
                selection.SelectionVersion,
                newSelectionJson,
                agentSnapshots,
                meetingInputs,
                policyJson,
                policyHash,
                ct).ConfigureAwait(false);
        }
        else
        {
            saved = await repository.TrySaveSessionSelectionAsync(
                parameters.SessionId,
                parameters.ExpectedSelectionVersion,
                selection.SelectionVersion,
                newSelectionJson,
                agentSnapshots,
                ct).ConfigureAwait(false);
        }

        if (!saved)
        {
            return Error(
                request.Id,
                -32006,
                $"Selection version {parameters.ExpectedSelectionVersion} is no longer current.");
        }

        _sessionSelections[parameters.SessionId] = selection;
        var payload = JsonSerializer.SerializeToElement(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionRehydrateResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteMeetingRepository meetingRepository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionRehydrateParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.AgentDefinitions is null)
        {
            return Error(request.Id, -32602, "The session.rehydrate parameters are invalid.");
        }

        var duplicateDefinition = parameters.AgentDefinitions
            .GroupBy(static definition => definition.AgentRef.AgentId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            return Error(request.Id, -32602, "Agent definitions must have unique Agent IDs.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = new List<string>();
        var persistedSelection = DeserializePersistedSelection(snapshot, diagnostics);
        if (persistedSelection is null || snapshot.AgentSnapshots.Length == 0)
        {
            var failed = new SessionRehydrateResult(
                snapshot.SessionId,
                "Failed",
                snapshot.AgentSnapshots.Select(static agent => agent.AgentId).ToArray());
            var failedPayload = JsonSerializer.SerializeToElement(
                failed,
                RuntimeJsonContext.Default.SessionRehydrateResult);
            return new JsonRpcResponse("2.0", request.Id, failedPayload);
        }

        var definitions = parameters.AgentDefinitions.ToDictionary(
            static definition => definition.AgentRef.AgentId,
            StringComparer.Ordinal);
        var mismatched = new List<string>();
        var missing = false;
        foreach (var agent in snapshot.AgentSnapshots)
        {
            if (!definitions.TryGetValue(agent.AgentId, out var definition))
            {
                missing = true;
                continue;
            }

            if (!string.Equals(
                    definition.AgentRef.PromptTemplateVersion,
                    agent.PromptTemplateVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RuntimeRunMetadata.ComputePromptHash(definition.AgentRef.SystemPrompt),
                    agent.PromptHash,
                    StringComparison.Ordinal))
            {
                mismatched.Add(agent.AgentId);
            }
        }

        var status = mismatched.Count > 0
            ? "Failed"
            : missing
                ? "PartiallyReady"
                : "Ready";
        if (string.Equals(status, "Ready", StringComparison.Ordinal))
        {
            _sessionSelections[snapshot.SessionId] = RehydrateSelection(
                persistedSelection,
                definitions);
            if (snapshot.Mode == RuntimeMode.Meeting)
            {
                await ResumeRehydratedMeetingInvocationAsync(
                    snapshot.SessionId,
                    session,
                    meetingRepository,
                    outbox,
                    ct).ConfigureAwait(false);
            }
        }

        var result = new SessionRehydrateResult(snapshot.SessionId, status, [.. mismatched]);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionRehydrateResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateNewSessionRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params
            ?? throw new InvalidDataException("run.new_session requires request parameters.");
        var runRequest = parameters.Deserialize(
            RuntimeJsonContext.Default.NewSessionRunRequest)
            ?? throw new InvalidDataException("run.new_session parameters were empty.");
        if (runRequest.Mode != runRequest.Selection.Mode)
        {
            return Error(request.Id, -32602, "The Run mode and Selection mode must match.");
        }

        switch (runRequest.Mode)
        {
            case RuntimeMode.Expert:
                if (runRequest.Selection.ModeOptions is not ExpertModeOptions)
                {
                    return Error(request.Id, -32602, "Expert mode requires ExpertModeOptions.");
                }
                break;
            case RuntimeMode.Meeting:
                if (runRequest.Selection.ModeOptions is not MeetingModeOptions)
                {
                    return Error(request.Id, -32602, "Meeting mode requires MeetingModeOptions.");
                }
                break;
            case RuntimeMode.Work:
                if (runRequest.Selection.ModeOptions is not WorkModeOptions workOptions)
                {
                    return Error(request.Id, -32602, "Work mode requires WorkModeOptions.");
                }

                if (workOptions.AvailableAgents is null || workOptions.AvailableAgents.Length == 0)
                {
                    return Error(request.Id, -32602, "Work mode requires at least one available child Agent.");
                }

                try
                {
                    workOptions.EffectiveWorkflowPolicy.Validate();
                }
                catch (ArgumentException ex)
                {
                    return Error(request.Id, -32602, ex.Message);
                }

                break;
            default:
                return Error(request.Id, -32602, $"Run mode '{runRequest.Mode}' is not supported.");
        }

        var plan = new RunStartPlan(
            $"new:{runRequest.SessionIdempotencyKey}",
            RuntimeRunMetadata.ComputeRequestHash(runRequest),
            runRequest,
            ExistingSessionId: null,
            runRequest.SessionIdempotencyKey,
            runRequest.Selection);
        return await StartRunResponseAsync(
            request,
            session,
            repository,
            outbox,
            plan,
            ct).ConfigureAwait(false);
    }

    private async Task<JsonRpcResponse> CreateExistingSessionRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params
            ?? throw new InvalidDataException("run.existing_session requires request parameters.");
        var existingRun = parameters.Deserialize(
            RuntimeJsonContext.Default.ExistingSessionRunRequest)
            ?? throw new InvalidDataException("run.existing_session parameters were empty.");
        if (string.IsNullOrWhiteSpace(existingRun.SessionId)
            || string.IsNullOrWhiteSpace(existingRun.RunIdempotencyKey)
            || existingRun.InputOverride is not { Length: > 0 })
        {
            return Error(request.Id, -32602, "run.existing_session parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(existingRun.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{existingRun.SessionId}' was not found.");
        }

        if (!_sessionSelections.TryGetValue(existingRun.SessionId, out var savedSelection))
        {
            return Error(
                request.Id,
                -32006,
                $"Session '{existingRun.SessionId}' requires session.rehydrate before it can run.");
        }

        if (existingRun.ExpectedSelectionVersion is { } expectedVersion
            && expectedVersion != savedSelection.SelectionVersion)
        {
            return Error(
                request.Id,
                -32006,
                $"Selection version {expectedVersion} does not match current version {savedSelection.SelectionVersion}.");
        }

        var effectiveSelection = existingRun.TurnOverride ?? savedSelection;
        if (effectiveSelection.Mode != snapshot.Mode)
        {
            return Error(request.Id, -32602, "run.existing_session selection mode does not match the session mode.");
        }

        switch (snapshot.Mode)
        {
            case RuntimeMode.Expert:
                if (effectiveSelection.ModeOptions is not ExpertModeOptions expertOptions)
                {
                    return Error(request.Id, -32602, "run.existing_session Expert mode requires ExpertModeOptions.");
                }

                if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt)
                    && existingRun.TurnOverride is null)
                {
                    return Error(
                        request.Id,
                        -32006,
                        "A TurnOverride with the current Agent prompt is required to resume this Session.");
                }

                var promptValidation = ValidateResumedExpertPrompt(snapshot, expertOptions);
                if (promptValidation is not null)
                {
                    return Error(request.Id, -32006, promptValidation);
                }
                break;

            case RuntimeMode.Meeting:
                if (effectiveSelection.ModeOptions is not MeetingModeOptions)
                {
                    return Error(request.Id, -32602, "run.existing_session Meeting mode requires MeetingModeOptions.");
                }
                break;

            default:
                return Error(request.Id, -32602, "run.existing_session only supports Expert and Meeting modes.");
        }

        var executionRequest = new NewSessionRunRequest(
            $"existing:{existingRun.SessionId}",
            existingRun.RunIdempotencyKey,
            snapshot.Mode,
            effectiveSelection,
            existingRun.InputOverride);
        var plan = new RunStartPlan(
            $"session:{existingRun.SessionId}",
            RuntimeRunMetadata.ComputeRequestHash(existingRun),
            executionRequest,
            existingRun.SessionId,
            SessionIdempotencyKey: null,
            InitialSelection: null);
        return await StartRunResponseAsync(
            request,
            session,
            repository,
            outbox,
            plan,
            ct).ConfigureAwait(false);
    }

    private IRuntimeProviderAdapter ResolveProviderAdapter(
        NextTurnSelection selection,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var providerSelection = selection with
        {
            DefaultSelection = selection.DefaultSelection with { ProviderId = providerId }
        };
        var adapter = _providerResolver?.Invoke(providerSelection)
            ?? new UnavailableProviderAdapter(providerId);

        if (!string.Equals(adapter.ProviderId, providerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resolved adapter ProviderId '{adapter.ProviderId}' does not match requested '{providerId}'.");
        }

        return adapter;
    }

    private async Task<JsonRpcResponse> StartRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        RunStartPlan plan,
        CancellationToken ct)
    {
        var runRequest = plan.ExecutionRequest;
        var primaryProviderId = runRequest.Selection.ModeOptions switch
        {
            ExpertModeOptions expert => expert.Agent.ProviderId
                ?? runRequest.Selection.DefaultSelection.ProviderId,
            MeetingModeOptions => runRequest.Selection.DefaultSelection.ProviderId,
            WorkModeOptions work => work.GeneralManager.ProviderId
                ?? runRequest.Selection.DefaultSelection.ProviderId,
            _ => throw new NotSupportedException(
                $"Mode '{runRequest.Selection.ModeOptions.GetType().Name}' is not supported.")
        };
        if (_activeRunRequests.TryGetValue(
            runRequest.RunIdempotencyKey,
            out var existingRequest))
        {
            if (!string.Equals(
                existingRequest.SessionIdentity,
                plan.SessionIdentity,
                StringComparison.Ordinal))
            {
                return Error(
                    request.Id,
                    -32004,
                    "The Run idempotency key is already bound to another Session request.");
            }

            if (!string.Equals(
                existingRequest.RequestHash,
                plan.RequestHash,
                StringComparison.Ordinal))
            {
                return Error(
                    request.Id,
                    -32004,
                    "The Run idempotency key was reused with a different request payload.");
            }

            var existingPayload = JsonSerializer.SerializeToElement(
                existingRequest.Accepted,
                RuntimeJsonContext.Default.RunAcceptedEvent);
            return new JsonRpcResponse("2.0", request.Id, Result: existingPayload);
        }

        IRuntimeProviderAdapter provider;
        try
        {
            provider = ResolveProviderAdapter(runRequest.Selection, primaryProviderId);
        }
        catch (Exception ex)
        {
            return Error(request.Id, -32602, $"The selected Provider is invalid: {ex.Message}");
        }

        var providerCache = new ConcurrentDictionary<string, Lazy<IRuntimeProviderAdapter>>(
            StringComparer.Ordinal);
        providerCache[primaryProviderId] = new Lazy<IRuntimeProviderAdapter>(
            () => provider,
            LazyThreadSafetyMode.ExecutionAndPublication);

        IRuntimeProviderAdapter ResolveRunProvider(string id)
        {
            return providerCache.GetOrAdd(id, key => new Lazy<IRuntimeProviderAdapter>(
                () => ResolveProviderAdapter(runRequest.Selection, key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        var runLease = _runRegistry.TryAcquire(provider.ProviderId, out var capacityResult);
        if (runLease is null)
        {
            var message = capacityResult is RunCapacityResult.ProviderCapacityReached
                ? $"The Provider '{provider.ProviderId}' has reached its Run capacity."
                : "The Runtime has reached its Run capacity.";
            return Error(request.Id, -32003, message);
        }

        var capacityTransferred = false;
        try
        {
            var sessionId = plan.ExistingSessionId;
            if (sessionId is null)
            {
                sessionId = await repository.CreateSessionAsync(
                    runRequest.Mode,
                    plan.SessionIdempotencyKey!,
                    TimeSpan.FromDays(7),
                    plan.RequestHash,
                    ct).ConfigureAwait(false);
                var initialSelection = plan.InitialSelection!;
                var persistedSelection = RuntimeRunMetadata.RemovePromptContent(initialSelection);
                var selectionJson = JsonSerializer.Serialize(
                    persistedSelection,
                    RuntimeJsonContext.Default.NextTurnSelection);
                var saved = await repository.TrySaveSessionSelectionAsync(
                    sessionId,
                    expectedSelectionVersion: null,
                    initialSelection.SelectionVersion,
                    selectionJson,
                    RuntimeRunMetadata.CreateAgentSnapshots(initialSelection),
                    ct).ConfigureAwait(false);
                if (!saved)
                {
                    return Error(request.Id, -32006, "The Session Selection could not be initialized.");
                }

                _sessionSelections[sessionId] = initialSelection;
            }

            if (!runLease.TryBindSession(sessionId))
            {
                return Error(
                    request.Id,
                    -32003,
                    $"Session '{sessionId}' has reached its Run capacity.");
            }

            var runId = await repository.CreateRunAsync(
                sessionId,
                runRequest.RunIdempotencyKey,
                TimeSpan.FromHours(24),
                plan.RequestHash,
                ct).ConfigureAwait(false);
            var shouldEmitAccepted = await repository.GetRunStatusAsync(runId, ct)
                .ConfigureAwait(false) is RunStatus.Accepted;
            var accepted = new RunAcceptedEvent(sessionId, runId);
            var payload = JsonSerializer.SerializeToElement(
                accepted,
                RuntimeJsonContext.Default.RunAcceptedEvent);
            if (!shouldEmitAccepted)
            {
                return new JsonRpcResponse("2.0", request.Id, Result: payload);
            }

            const long runSequence = 0;
            await outbox.AppendAsync(
                runId,
                runSequence,
                MessageTypes.RunAccepted,
                payload.GetRawText(),
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);

            var activeRun = new ActiveRun(runLease, session.HostInstanceId, provider);
            if (!_activeRuns.TryAdd(runId, activeRun))
            {
                activeRun.Dispose();
                return new JsonRpcResponse("2.0", request.Id, Result: payload);
            }

            _activeRunRequests[runRequest.RunIdempotencyKey] = new ActiveRunRequest(
                plan.SessionIdentity,
                plan.RequestHash,
                accepted);

            var toolCatalogSnapshot = session.ToolCatalogStore.CaptureSnapshot();
            var controlPeer = session.SupportsReverseRpc
                ? new ReconnectableDuplexRpcPeer(
                    () => ResolveReverseRpcPeer(session.HostInstanceId))
                : null;
            var executionTask = Task.Run(
                () => ExecuteRunAsync(
                    runId,
                    sessionId,
                    runRequest,
                    provider,
                    ResolveRunProvider,
                    session,
                    activeRun,
                    toolCatalogSnapshot,
                    controlPeer),
                CancellationToken.None);
            activeRun.Attach(executionTask);
            capacityTransferred = true;
            return new JsonRpcResponse("2.0", request.Id, Result: payload);
        }
        finally
        {
            if (!capacityTransferred)
            {
                runLease.Dispose();
            }
        }
    }

    private async Task ExecuteRunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest runRequest,
        IRuntimeProviderAdapter provider,
        Func<string, IRuntimeProviderAdapter> providerFactory,
        AuthenticatedSession session,
        ActiveRun activeRun,
        ToolCatalogSnapshot toolCatalogSnapshot,
        IDuplexRpcPeer? controlPeer,
        long meetingStartRunSequence = 1,
        MeetingHitlResponse? resumedMeetingHitlResponse = null,
        MeetingInvocationRecord? resumedMeetingInvocation = null,
        ConversationRecordV1? resumedCanonicalMessage = null,
        RuntimeError? resumedMeetingFailure = null)
    {
        var ct = activeRun.CancellationToken;
        try
        {
            await using var dispatchConnection = await OpenDatabaseConnectionAsync(ct)
                .ConfigureAwait(false);
            var runRepository = new SqliteSessionRepository(dispatchConnection);
            using var dispatchOutbox = CreateEventOutbox(dispatchConnection);
            var meetingRepository = new SqliteMeetingRepository(dispatchConnection);
            var isExpertRun = runRequest.Selection.ModeOptions is ExpertModeOptions;
            Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? meetingHitlHandler =
                isExpertRun || resumedMeetingHitlResponse is not null
                    ? null
                    : (request, hitlCt) => RequestMeetingHitlAsync(
                        request,
                        sessionId,
                        runId,
                        meetingRepository,
                        dispatchOutbox,
                        activeRun,
                        session,
                        hitlCt);
            await foreach (var envelope in _executionCore.RunAsync(
                runId,
                sessionId,
                runRequest,
                provider,
                session.ToolCatalogStore,
                toolCatalogSnapshot,
                controlPeer,
                (refresh, refreshCt) => RequestCredentialsAsync(
                    refresh,
                    session,
                    dispatchOutbox,
                    activeRun,
                    refreshCt),
                () => activeRun.IsTimedOut,
                providerFactory: providerFactory,
                meetingHitlHandler: meetingHitlHandler,
                meetingStartRunSequence: meetingStartRunSequence,
                resumedMeetingHitlResponse: resumedMeetingHitlResponse,
                resumedMeetingInvocation: resumedMeetingInvocation,
                resumedCanonicalMessage: resumedCanonicalMessage,
                resumedMeetingFailure: resumedMeetingFailure,
                ct: ct).ConfigureAwait(false))
            {
                if (IsTerminalRunEvent(envelope.MessageType))
                {
                    await TransitionRunForTerminalEventAsync(
                        runRepository,
                        runId,
                        envelope.MessageType).ConfigureAwait(false);
                    activeRun.ReleaseCapacity();
                }

                await DispatchPendingEventsAsync(session, dispatchOutbox, _shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            activeRun.ReleaseCapacity();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && activeRun.IsTimedOut)
        {
            activeRun.ReleaseCapacity();
            await CompleteFailedRunAsync(
                runId,
                sessionId,
                session,
                new TimeoutException("The Run exceeded its configured timeout."),
                RuntimeErrorCodes.RunTimedOut).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activeRun.ReleaseCapacity();
            await CompleteCancelledRunAsync(runId, sessionId, session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activeRun.ReleaseCapacity();
            await CompleteFailedRunAsync(runId, sessionId, session, ex).ConfigureAwait(false);
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            _activeRunRequests.TryRemove(runRequest.RunIdempotencyKey, out _);
            activeRun.Dispose();
        }
    }

    private IDuplexRpcPeer? ResolveReverseRpcPeer(string hostInstanceId)
    {
        if (!_sessions.TryGetValue(hostInstanceId, out var session)
            || !session.SupportsReverseRpc)
        {
            return null;
        }

        return session.TryGetControlPeer();
    }

    private async Task<bool> RequestCredentialsAsync(
        CredentialsRefreshRequestedEvent request,
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        ActiveRun activeRun,
        CancellationToken ct)
    {
        var waitTask = activeRun.WaitForCredentialsAsync(request.ProviderId, ct);
        if (waitTask.IsCompletedSuccessfully && !await waitTask.ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.CredentialsRefreshRequestedEvent);
            var runSequence = await outbox.GetNextRunSequenceAsync(request.RunId, ct)
                .ConfigureAwait(false);
            await outbox.AppendAsync(
                request.RunId,
                runSequence,
                MessageTypes.CredentialsRefreshRequested,
                payload.GetRawText(),
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);
            return await waitTask.ConfigureAwait(false);
        }
        catch
        {
            activeRun.CancelCredentialWait();
            throw;
        }
    }

    private async Task RecoverPersistedMeetingApprovalsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<MeetingApprovalRecord> approvals;
            await using (var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false))
            {
                var meetingRepository = new SqliteMeetingRepository(connection);
                approvals = await meetingRepository.ListRecoverableApprovalsAsync(ct)
                    .ConfigureAwait(false);
            }

            var recoveries = new List<(string ApprovalRequestId, DateTimeOffset DueAt)>();

            foreach (var approval in approvals)
            {
                if (string.Equals(approval.Status, "Pending", StringComparison.Ordinal))
                {
                    var request = DeserializeMeetingHitlRequest(approval.ApprovalJson);
                    if (request?.ExpiresAt is { } expiresAt
                        && string.Equals(
                            request.ApprovalRequestId,
                            approval.ApprovalRequestId,
                            StringComparison.Ordinal))
                    {
                        recoveries.Add((approval.ApprovalRequestId, expiresAt));
                    }

                    continue;
                }

                var response = DeserializeMeetingHitlResponse(approval.ApprovalJson);
                if (IsMeetingHitlTimeoutResponse(response, approval.ApprovalRequestId))
                {
                    recoveries.Add((approval.ApprovalRequestId, DateTimeOffset.MinValue));
                }
            }

            foreach (var recovery in recoveries.OrderBy(static item => item.DueAt))
            {
                var delay = recovery.DueAt - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }

                await FinalizeRecoveredMeetingApprovalTimeoutAsync(
                    recovery.ApprovalRequestId,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task FinalizeRecoveredMeetingApprovalTimeoutAsync(
        string approvalRequestId,
        CancellationToken ct)
    {
        if (_activeRuns.Values.Any(run => run.HasMeetingHitlWaiter(approvalRequestId)))
        {
            return;
        }

        await _meetingResumeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activeRuns.Values.Any(run => run.HasMeetingHitlWaiter(approvalRequestId)))
            {
                return;
            }

            await using var connection = await OpenDatabaseConnectionAsync(ct)
                .ConfigureAwait(false);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var approval = await meetingRepository.FindApprovalByRequestIdAsync(
                    approvalRequestId,
                    ct)
                .ConfigureAwait(false);
            if (approval is null)
            {
                return;
            }

            MeetingHitlResponse timeoutResponse;
            string timeoutJson;
            if (string.Equals(approval.Status, "Pending", StringComparison.Ordinal))
            {
                var pendingRequest = DeserializeMeetingHitlRequest(approval.ApprovalJson);
                if (pendingRequest?.ExpiresAt is not { } expiresAt
                    || expiresAt > _timeProvider.GetUtcNow()
                    || !string.Equals(
                        pendingRequest.ApprovalRequestId,
                        approvalRequestId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                timeoutResponse = new MeetingHitlResponse(
                    approvalRequestId,
                    MeetingHitlAction.Cancel,
                    Supplement: null,
                    Reason: MeetingHitlTimeoutReason,
                    RespondedAt: _timeProvider.GetUtcNow());
                timeoutJson = JsonSerializer.Serialize(
                    timeoutResponse,
                    RuntimeJsonContext.Default.MeetingHitlResponse);
                await meetingRepository.ResolvePendingApprovalAsync(
                    approval.SessionId,
                    approvalRequestId,
                    timeoutJson,
                    "Decided",
                    ct).ConfigureAwait(false);
            }
            else
            {
                timeoutResponse = DeserializeMeetingHitlResponse(approval.ApprovalJson)
                    ?? throw new InvalidDataException(
                        $"Meeting approval '{approvalRequestId}' has an invalid persisted response.");
                if (!IsMeetingHitlTimeoutResponse(timeoutResponse, approvalRequestId))
                {
                    return;
                }

                timeoutJson = approval.ApprovalJson
                    ?? throw new InvalidDataException(
                        $"Meeting approval '{approvalRequestId}' has no persisted response.");
            }

            using var outbox = new SqliteEventOutbox(connection);
            if (!await outbox.ContainsAsync(
                    approval.RunId,
                    MessageTypes.MeetingHitlResponse,
                    timeoutJson,
                    ct).ConfigureAwait(false))
            {
                var responseRunSequence = await outbox.GetNextRunSequenceAsync(
                    approval.RunId,
                    ct).ConfigureAwait(false);
                await outbox.AppendAsync(
                    approval.RunId,
                    responseRunSequence,
                    MessageTypes.MeetingHitlResponse,
                    timeoutJson,
                    ct).ConfigureAwait(false);
            }

            var cancelledJson = JsonSerializer.Serialize(
                new RunCancelledEvent(approval.RunId, approval.SessionId),
                RuntimeJsonContext.Default.RunCancelledEvent);
            if (!await outbox.ContainsAsync(
                    approval.RunId,
                    MessageTypes.RunCancelled,
                    cancelledJson,
                    ct).ConfigureAwait(false))
            {
                var cancelledRunSequence = await outbox.GetNextRunSequenceAsync(
                    approval.RunId,
                    ct).ConfigureAwait(false);
                await outbox.AppendAsync(
                    approval.RunId,
                    cancelledRunSequence,
                    MessageTypes.RunCancelled,
                    cancelledJson,
                    ct).ConfigureAwait(false);
            }

            var runRepository = new SqliteSessionRepository(connection);
            var runStatus = await runRepository.GetRunStatusAsync(approval.RunId, ct)
                .ConfigureAwait(false);
            if (runStatus == RunStatus.WaitingForApproval)
            {
                await runRepository.TransitionRunToTerminalAsync(
                    approval.RunId,
                    RunStatus.WaitingForApproval,
                    RunStatus.Cancelled,
                    MeetingHitlTimeoutReason,
                    ct).ConfigureAwait(false);
            }
            else if (runStatus != RunStatus.Cancelled)
            {
                throw new InvalidOperationException(
                    $"Run '{approval.RunId}' is in state '{runStatus}' and cannot finalize a recovered HITL timeout.");
            }

            var meetingCancelled = await meetingRepository.TryTransitionMeetingStatusAsync(
                approval.SessionId,
                "WaitingForApproval",
                "Cancelled",
                ct).ConfigureAwait(false);
            if (!meetingCancelled)
            {
                var snapshot = await meetingRepository.GetMeetingSnapshotAsync(
                        approval.SessionId,
                        ct)
                    .ConfigureAwait(false);
                if (!string.Equals(snapshot?.Session.Status, "Cancelled", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Meeting session '{approval.SessionId}' could not finalize a recovered HITL timeout.");
                }
            }

            foreach (var authenticatedSession in _sessions.Values)
            {
                await DispatchPendingEventsAsync(authenticatedSession, outbox, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _meetingResumeGate.Release();
        }
    }

    private static MeetingHitlRequest? DeserializeMeetingHitlRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                json,
                RuntimeJsonContext.Default.MeetingHitlRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MeetingHitlResponse? DeserializeMeetingHitlResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                json,
                RuntimeJsonContext.Default.MeetingHitlResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsMeetingHitlTimeoutResponse(
        MeetingHitlResponse? response,
        string approvalRequestId) =>
        response is
        {
            Action: MeetingHitlAction.Cancel,
            Reason: MeetingHitlTimeoutReason
        }
        && string.Equals(
            response.ApprovalRequestId,
            approvalRequestId,
            StringComparison.Ordinal);

    private async Task<MeetingHitlResponse> RequestMeetingHitlAsync(
        MeetingHitlRequest request,
        string sessionId,
        string runId,
        SqliteMeetingRepository meetingRepository,
        SqliteEventOutbox outbox,
        ActiveRun activeRun,
        AuthenticatedSession session,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            request,
            RuntimeJsonContext.Default.MeetingHitlRequest);

        await meetingRepository.SavePendingApprovalAsync(
            sessionId,
            request.ApprovalRequestId,
            json,
            "Pending",
            ct).ConfigureAwait(false);

        await meetingRepository.TryTransitionMeetingStatusAsync(
            sessionId,
            "Running",
            "WaitingForApproval",
            ct).ConfigureAwait(false);

        await using var runConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        var runRepository = new SqliteSessionRepository(runConnection);
        await runRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.WaitingForApproval,
            ct).ConfigureAwait(false);

        var waitTask = activeRun.WaitForMeetingHitlAsync(request, ct);

        var runSequence = await outbox.GetNextRunSequenceAsync(runId, ct)
            .ConfigureAwait(false);
        await outbox.AppendAsync(
            runId,
            runSequence,
            MessageTypes.MeetingHitlRequest,
            json,
            ct).ConfigureAwait(false);
        await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);

        try
        {
            var response = await waitTask.ConfigureAwait(false);
            await meetingRepository.TryTransitionMeetingStatusAsync(
                sessionId,
                "WaitingForApproval",
                "Running",
                ct).ConfigureAwait(false);
            await runRepository.TransitionRunStatusAsync(
                runId,
                RunStatus.WaitingForApproval,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var persistedApproval = await meetingRepository.FindApprovalByRequestIdAsync(
                request.ApprovalRequestId,
                CancellationToken.None).ConfigureAwait(false);
            if (string.Equals(persistedApproval?.Status, "Pending", StringComparison.Ordinal))
            {
                var cancelledResponse = new MeetingHitlResponse(
                    request.ApprovalRequestId,
                    MeetingHitlAction.Cancel,
                    Supplement: null,
                    Reason: "Run cancelled while waiting for HITL approval.",
                    RespondedAt: DateTimeOffset.UtcNow);
                var cancelledJson = JsonSerializer.Serialize(
                    cancelledResponse,
                    RuntimeJsonContext.Default.MeetingHitlResponse);
                await meetingRepository.ResolvePendingApprovalAsync(
                    sessionId,
                    request.ApprovalRequestId,
                    cancelledJson,
                    "Decided",
                    CancellationToken.None).ConfigureAwait(false);
                var responseRunSequence = await outbox.GetNextRunSequenceAsync(
                    runId,
                    CancellationToken.None).ConfigureAwait(false);
                await outbox.AppendAsync(
                    runId,
                    responseRunSequence,
                    MessageTypes.MeetingHitlResponse,
                    cancelledJson,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (TimeoutException)
        {
            var timeoutResponse = new MeetingHitlResponse(
                request.ApprovalRequestId,
                MeetingHitlAction.Cancel,
                Supplement: null,
                Reason: MeetingHitlTimeoutReason,
                RespondedAt: DateTimeOffset.UtcNow);
            var timeoutJson = JsonSerializer.Serialize(
                timeoutResponse,
                RuntimeJsonContext.Default.MeetingHitlResponse);
            await meetingRepository.ResolvePendingApprovalAsync(
                sessionId,
                request.ApprovalRequestId,
                timeoutJson,
                "Decided",
                ct).ConfigureAwait(false);
            var responseRunSequence = await outbox.GetNextRunSequenceAsync(runId, ct)
                .ConfigureAwait(false);
            await outbox.AppendAsync(
                runId,
                responseRunSequence,
                MessageTypes.MeetingHitlResponse,
                timeoutJson,
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);
            await meetingRepository.TryTransitionMeetingStatusAsync(
                sessionId,
                "WaitingForApproval",
                "Running",
                ct).ConfigureAwait(false);
            await runRepository.TransitionRunStatusAsync(
                runId,
                RunStatus.WaitingForApproval,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            return timeoutResponse;
        }
        finally
        {
            if (!waitTask.IsCompletedSuccessfully)
            {
                activeRun.CancelMeetingHitlWait();
            }
        }
    }

    private async Task ResumePersistedMeetingHitlAsync(
        MeetingApprovalRecord approval,
        MeetingHitlResponse response,
        AuthenticatedSession session,
        SqliteMeetingRepository meetingRepository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        await _meetingResumeGate.WaitAsync(ct).ConfigureAwait(false);
        RecoveredMeetingRunStart? start = null;
        try
        {
            if (_activeRuns.ContainsKey(approval.RunId))
            {
                return;
            }

            var current = await meetingRepository.FindApprovalByRequestIdAsync(
                    approval.ApprovalRequestId,
                    ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Meeting approval '{approval.ApprovalRequestId}' no longer exists.");
            if (string.Equals(current.Status, "Decided", StringComparison.Ordinal))
            {
                if (!IsSameMeetingHitlResponse(current.ApprovalJson, response))
                {
                    throw new InvalidOperationException(
                        $"Meeting approval '{approval.ApprovalRequestId}' was decided with a different response.");
                }
            }
            else if (!string.Equals(current.Status, "Pending", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Meeting approval '{approval.ApprovalRequestId}' is in unsupported state '{current.Status}'.");
            }

            start = await PrepareRecoveredMeetingRunAsync(
                current.SessionId,
                current.RunId,
                session,
                ct)
                .ConfigureAwait(false);

            if (string.Equals(current.Status, "Pending", StringComparison.Ordinal))
            {
                var responseJson = JsonSerializer.Serialize(
                    response,
                    RuntimeJsonContext.Default.MeetingHitlResponse);
                await meetingRepository.ResolvePendingApprovalAsync(
                    current.SessionId,
                    current.ApprovalRequestId,
                    responseJson,
                    "Decided",
                    ct).ConfigureAwait(false);
                var responseRunSequence = await outbox.GetNextRunSequenceAsync(
                    current.RunId,
                    ct).ConfigureAwait(false);
                await outbox.AppendAsync(
                    current.RunId,
                    responseRunSequence,
                    MessageTypes.MeetingHitlResponse,
                    responseJson,
                    ct).ConfigureAwait(false);
                await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);
            }

            var executionRunSequence = await outbox.GetNextRunSequenceAsync(current.RunId, ct)
                .ConfigureAwait(false);
            LaunchRecoveredMeetingRun(start, response, session, executionRunSequence);
            start = null;
        }
        finally
        {
            start?.Lease.Dispose();
            _meetingResumeGate.Release();
        }
    }

    private async Task ResumeRehydratedMeetingInvocationAsync(
        string sessionId,
        AuthenticatedSession session,
        SqliteMeetingRepository meetingRepository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        await _meetingResumeGate.WaitAsync(ct).ConfigureAwait(false);
        RecoveredMeetingRunStart? start = null;
        try
        {
            var meeting = await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct)
                .ConfigureAwait(false);
            if (meeting is null
                || !string.Equals(meeting.Session.Status, "Running", StringComparison.Ordinal))
            {
                return;
            }

            var recoverable = await meetingRepository.QueryRecoverableInvocationsAsync(sessionId, ct)
                .ConfigureAwait(false);
            if (recoverable.Count == 0)
            {
                return;
            }

            if (recoverable.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{sessionId}' has {recoverable.Count} recoverable Invocations; expected exactly one.");
            }

            var invocation = recoverable[0];
            if (_activeRuns.ContainsKey(invocation.RunId))
            {
                return;
            }

            var canonicalMessages = await new ConversationStore(_dataDirectory)
                .FindMessagesByInvocationIdAsync(
                    sessionId,
                    invocation.InvocationId,
                    ct).ConfigureAwait(false);
            var canonicalMessage = ValidateRecoverableCanonicalMessage(
                invocation,
                canonicalMessages);

            start = await PrepareRecoveredMeetingRunAsync(
                sessionId,
                invocation.RunId,
                session,
                ct).ConfigureAwait(false);
            var meetingOptions = (MeetingModeOptions)start.Request.Selection.ModeOptions;
            var retryExhausted = canonicalMessage is null
                && string.Equals(invocation.Status, "Interrupted", StringComparison.Ordinal)
                && invocation.RetryCount >= meetingOptions.EffectivePolicy.MaxRetriesPerInvocation;
            var resumedFailure = retryExhausted
                ? new RuntimeError(
                    "meeting_recovery_retry_exhausted",
                    "orchestration",
                    $"Invocation '{invocation.InvocationId}' exhausted its recovery retry limit of "
                    + $"{meetingOptions.EffectivePolicy.MaxRetriesPerInvocation}.",
                    IsRetryable: false,
                    ProviderDetails: null,
                    Guid.NewGuid().ToString("N"))
                : null;
            var resumed = await meetingRepository.ResumeRecoverableInvocationAsync(
                invocation.InvocationId,
                meetingOptions.EffectivePolicy.MaxRetriesPerInvocation,
                resumeWithoutProvider: canonicalMessage is not null || resumedFailure is not null,
                ct).ConfigureAwait(false);
            start = start with
            {
                ResumedInvocation = resumed,
                ResumedCanonicalMessage = canonicalMessage,
                ResumedFailure = resumedFailure
            };

            var executionRunSequence = await outbox.GetNextRunSequenceAsync(
                invocation.RunId,
                ct).ConfigureAwait(false);
            LaunchRecoveredMeetingRun(
                start,
                response: null,
                session,
                executionRunSequence);
            start = null;
        }
        finally
        {
            start?.Lease.Dispose();
            _meetingResumeGate.Release();
        }
    }

    private async Task<RecoveredMeetingRunStart> PrepareRecoveredMeetingRunAsync(
        string sessionId,
        string runId,
        AuthenticatedSession session,
        CancellationToken ct)
    {
        if (!_sessionSelections.TryGetValue(sessionId, out var selection))
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' requires session.rehydrate before its meeting Run can resume.");
        }

        if (selection.Mode != RuntimeMode.Meeting
            || selection.ModeOptions is not MeetingModeOptions)
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' does not contain a rehydrated Meeting Selection.");
        }

        var initialInput = await LoadMeetingInitialInputAsync(sessionId, ct)
            .ConfigureAwait(false);
        var primaryProviderId = selection.DefaultSelection.ProviderId;
        var provider = ResolveProviderAdapter(selection, primaryProviderId);
        var providerCache = new ConcurrentDictionary<string, Lazy<IRuntimeProviderAdapter>>(
            StringComparer.Ordinal)
        {
            [primaryProviderId] = new(
                () => provider,
                LazyThreadSafetyMode.ExecutionAndPublication)
        };

        IRuntimeProviderAdapter ResolveRunProvider(string providerId)
        {
            return providerCache.GetOrAdd(
                providerId,
                key => new Lazy<IRuntimeProviderAdapter>(
                    () => ResolveProviderAdapter(selection, key),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        var lease = _runRegistry.TryAcquire(provider.ProviderId, out var capacityResult)
            ?? throw new InvalidOperationException(
                capacityResult is RunCapacityResult.ProviderCapacityReached
                    ? $"The Provider '{provider.ProviderId}' has reached its Run capacity."
                    : "The Runtime has reached its Run capacity.");
        if (!lease.TryBindSession(sessionId))
        {
            lease.Dispose();
            throw new InvalidOperationException(
                $"Session '{sessionId}' has reached its Run capacity.");
        }

        var request = new NewSessionRunRequest(
            $"meeting-recovery:{sessionId}",
            $"meeting-recovery:{runId}",
            RuntimeMode.Meeting,
            selection,
            initialInput);
        return new RecoveredMeetingRunStart(
            sessionId,
            runId,
            request,
            provider,
            ResolveRunProvider,
            lease,
            ResumedInvocation: null);
    }

    private void LaunchRecoveredMeetingRun(
        RecoveredMeetingRunStart start,
        MeetingHitlResponse? response,
        AuthenticatedSession session,
        long startRunSequence)
    {
        var activeRun = new ActiveRun(start.Lease, session.HostInstanceId, start.Provider);
        if (!_activeRuns.TryAdd(start.RunId, activeRun))
        {
            activeRun.Dispose();
            return;
        }

        try
        {
            var toolCatalogSnapshot = session.ToolCatalogStore.CaptureSnapshot();
            var controlPeer = session.SupportsReverseRpc
                ? new ReconnectableDuplexRpcPeer(
                    () => ResolveReverseRpcPeer(session.HostInstanceId))
                : null;
            var executionTask = Task.Run(
                () => ExecuteRunAsync(
                    start.RunId,
                    start.SessionId,
                    start.Request,
                    start.Provider,
                    start.ProviderFactory,
                    session,
                    activeRun,
                    toolCatalogSnapshot,
                    controlPeer,
                    startRunSequence,
                    response,
                    start.ResumedInvocation,
                    start.ResumedCanonicalMessage,
                    start.ResumedFailure),
                CancellationToken.None);
            activeRun.Attach(executionTask);
        }
        catch
        {
            _activeRuns.TryRemove(start.RunId, out _);
            activeRun.Dispose();
            throw;
        }
    }

    private async Task<ContentBlock[]> LoadMeetingInitialInputAsync(
        string sessionId,
        CancellationToken ct)
    {
        var history = await new ConversationStore(_dataDirectory)
            .ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var initial = history
            .Where(static record =>
                string.Equals(record.Role, "user", StringComparison.Ordinal)
                && string.Equals(record.AgentId, "meeting.user", StringComparison.Ordinal))
            .OrderBy(static record => record.Sequence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Meeting session '{sessionId}' has no recoverable initial input in canonical history.");
        return JsonSerializer.Deserialize(
                initial.Content,
                RuntimeJsonContext.Default.ContentBlockArray)
            ?? throw new InvalidOperationException(
                $"Meeting session '{sessionId}' has invalid initial input in canonical history.");
    }

    private static ConversationRecordV1? ValidateRecoverableCanonicalMessage(
        MeetingInvocationRecoveryItem invocation,
        IReadOnlyList<ConversationRecordV1> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        if (messages.Count != 1)
        {
            throw new InvalidDataException(
                $"Invocation '{invocation.InvocationId}' has {messages.Count} canonical messages; expected exactly one.");
        }

        var message = messages[0];
        var isParticipant = string.Equals(
            invocation.Role,
            "participant",
            StringComparison.OrdinalIgnoreCase);
        var isSummarizer = string.Equals(
            invocation.Role,
            "summarizer",
            StringComparison.OrdinalIgnoreCase);
        if ((!isParticipant && !isSummarizer)
            || !string.Equals(message.InvocationId, invocation.InvocationId, StringComparison.Ordinal)
            || !string.Equals(message.AgentId, invocation.AgentId, StringComparison.Ordinal)
            || !string.Equals(message.Role, "assistant", StringComparison.Ordinal)
            || (isParticipant && message.SummaryMetadata is not null)
            || (isSummarizer && message.SummaryMetadata is null))
        {
            throw new InvalidDataException(
                $"Invocation '{invocation.InvocationId}' has an invalid canonical recovery record.");
        }

        return message;
    }

    private static bool IsSameMeetingHitlResponse(
        string? persistedJson,
        MeetingHitlResponse response)
    {
        if (string.IsNullOrWhiteSpace(persistedJson))
        {
            return false;
        }

        try
        {
            var stored = JsonSerializer.Deserialize(
                persistedJson,
                RuntimeJsonContext.Default.MeetingHitlResponse);
            return stored is not null
                && string.Equals(
                    stored.ApprovalRequestId,
                    response.ApprovalRequestId,
                    StringComparison.Ordinal)
                && stored.Action == response.Action
                && string.Equals(stored.Supplement, response.Supplement, StringComparison.Ordinal)
                && string.Equals(stored.Reason, response.Reason, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task CompleteCancelledRunAsync(
        string runId,
        string sessionId,
        AuthenticatedSession session)
    {
        await using var repositoryConnection = await OpenDatabaseConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var repository = new SqliteSessionRepository(repositoryConnection);
        await TryTransitionMeetingToTerminalAsync(
            repositoryConnection,
            sessionId,
            "Cancelled").ConfigureAwait(false);
        if (!await TryTransitionToTerminalAsync(
            repository,
            runId,
            RunStatus.Cancelled).ConfigureAwait(false))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToElement(
            new RunCancelledEvent(runId, sessionId),
            RuntimeJsonContext.Default.RunCancelledEvent);
        await AppendTerminalEventAsync(
            runId,
            MessageTypes.RunCancelled,
            payload,
            session).ConfigureAwait(false);
    }

    private async Task CompleteFailedRunAsync(
        string runId,
        string sessionId,
        AuthenticatedSession session,
        Exception exception,
        string errorCode = "RunExecutionFailed")
    {
        await using var repositoryConnection = await OpenDatabaseConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var repository = new SqliteSessionRepository(repositoryConnection);
        await TryTransitionMeetingToTerminalAsync(
            repositoryConnection,
            sessionId,
            "Failed").ConfigureAwait(false);
        if (!await TryTransitionToTerminalAsync(
            repository,
            runId,
            RunStatus.Failed).ConfigureAwait(false))
        {
            return;
        }

        var error = new RuntimeError(
            errorCode,
            "runtime",
            exception.Message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));
        var payload = JsonSerializer.SerializeToElement(
            new RunFailedEvent(runId, sessionId, error),
            RuntimeJsonContext.Default.RunFailedEvent);
        await AppendTerminalEventAsync(
            runId,
            MessageTypes.RunFailed,
            payload,
            session).ConfigureAwait(false);
    }

    private static async Task TryTransitionMeetingToTerminalAsync(
        SqliteConnection connection,
        string sessionId,
        string targetStatus)
    {
        var meetingRepository = new SqliteMeetingRepository(connection);
        var snapshot = await meetingRepository.GetMeetingSnapshotAsync(
            sessionId,
            CancellationToken.None).ConfigureAwait(false);
        if (snapshot is null
            || snapshot.Session.Status is "Completed" or "Failed" or "Cancelled")
        {
            return;
        }

        await meetingRepository.TryTransitionMeetingStatusAsync(
            sessionId,
            snapshot.Session.Status,
            targetStatus,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AppendTerminalEventAsync(
        string runId,
        string messageType,
        JsonElement payload,
        AuthenticatedSession session)
    {
        await using var outboxConnection = await OpenDatabaseConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        using var outbox = CreateEventOutbox(outboxConnection);
        await using var sequenceCommand = outboxConnection.CreateCommand();
        sequenceCommand.CommandText = """
            SELECT COALESCE(MAX(run_sequence), -1) + 1
            FROM event_outbox
            WHERE run_id = $runId;
            """;
        sequenceCommand.Parameters.AddWithValue("$runId", runId);
        var runSequence = Convert.ToInt64(
            await sequenceCommand.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await outbox.AppendAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            CancellationToken.None).ConfigureAwait(false);
        await DispatchPendingEventsAsync(session, outbox, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task TransitionRunForTerminalEventAsync(
        SqliteSessionRepository repository,
        string runId,
        string messageType)
    {
        var status = string.Equals(messageType, MessageTypes.RunCompleted, StringComparison.Ordinal)
            ? RunStatus.Completed
            : string.Equals(messageType, MessageTypes.RunCancelled, StringComparison.Ordinal)
                ? RunStatus.Cancelled
                : RunStatus.Failed;
        await TryTransitionToTerminalAsync(repository, runId, status).ConfigureAwait(false);
    }

    private static async Task<bool> TryTransitionToTerminalAsync(
        SqliteSessionRepository repository,
        string runId,
        RunStatus terminalStatus)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await repository.GetRunStatusAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (Madorin.AI.Runtime.Core.RunStateMachine.IsTerminal(current))
            {
                return false;
            }

            if (terminalStatus is RunStatus.Failed && current is RunStatus.Accepted)
            {
                try
                {
                    await repository.TransitionRunStatusAsync(
                        runId,
                        RunStatus.Accepted,
                        RunStatus.Preparing,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                continue;
            }

            if (terminalStatus is RunStatus.Completed && current is RunStatus.Running)
            {
                try
                {
                    await repository.TransitionRunStatusAsync(
                        runId,
                        RunStatus.Running,
                        RunStatus.Persisting,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                continue;
            }

            try
            {
                await repository.TransitionRunToTerminalAsync(
                    runId,
                    current,
                    terminalStatus,
                    terminalText: null,
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    private async Task DispatchPendingEventsAsync(
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        if (_sessions.TryGetValue(session.HostInstanceId, out var currentSession))
        {
            session = currentSession;
        }

        await _eventDispatchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var eventChannel = session.TryGetEventChannel();
            if (eventChannel is null)
            {
                return;
            }

            var entries = await outbox.LoadPendingAsync(ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.Gsn <= session.LastDispatchedGsn)
                {
                    continue;
                }

                using var payloadDocument = JsonDocument.Parse(entry.PayloadJson);
                var envelope = new RuntimeEventEnvelope(
                    InstanceId,
                    entry.Gsn,
                    entry.RunId,
                    entry.RunSequence,
                    entry.MessageType,
                    DateTimeOffset.UtcNow,
                    payloadDocument.RootElement.Clone());
                try
                {
                    await eventChannel.SendAsync(envelope, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    session.DetachEventChannel(eventChannel);
                    return;
                }

                await outbox.MarkSentAsync(entry.Gsn, ct).ConfigureAwait(false);
                session.MarkDispatched(entry.Gsn);
            }
        }
        finally
        {
            _eventDispatchGate.Release();
        }
    }

    private async Task ReplayPendingEventsAsync(
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        FramedEventChannel eventChannel,
        CancellationToken ct)
    {
        await _eventDispatchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await outbox.LoadPendingAsync(ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.Gsn <= session.LastConfirmedGsn)
                {
                    continue;
                }

                using var payloadDocument = JsonDocument.Parse(entry.PayloadJson);
                var envelope = new RuntimeEventEnvelope(
                    InstanceId,
                    entry.Gsn,
                    entry.RunId,
                    entry.RunSequence,
                    entry.MessageType,
                    DateTimeOffset.UtcNow,
                    payloadDocument.RootElement.Clone());
                await eventChannel.SendAsync(envelope, ct).ConfigureAwait(false);
                await outbox.MarkSentAsync(entry.Gsn, ct).ConfigureAwait(false);
                session.MarkDispatched(entry.Gsn);
            }
        }
        finally
        {
            _eventDispatchGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_databaseConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqliteEventOutbox CreateEventOutbox(SqliteConnection connection) =>
        new(
            connection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);

    private RuntimeLimits CreateNegotiatedLimits(bool supportsStage5) =>
        _limits with
        {
            MaxEventFrameBytes = NamedPipeTransport.MaxEventMessageBytes,
            MaxToolRounds = supportsStage5 ? _limits.MaxToolRounds ?? 8 : null,
            MaxToolCallsPerRound = supportsStage5 ? _limits.MaxToolCallsPerRound ?? 8 : null,
            MaxToolResultBytesPerInvocation = supportsStage5
                ? _limits.MaxToolResultBytesPerInvocation ?? 4L * 1024 * 1024
                : null
        };

    private static JsonRpcResponse Error(long id, int code, string message)
        => new("2.0", id, Error: new JsonRpcError(code, message));

    private static bool IsTerminalRunEvent(string messageType) =>
        string.Equals(messageType, MessageTypes.RunCompleted, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.RunFailed, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.RunCancelled, StringComparison.Ordinal);

    private static RunQueryResult? CreateRunQueryResult(RunSnapshot? snapshot) =>
        snapshot is null
            ? null
            : new RunQueryResult(
                snapshot.RunId,
                snapshot.SessionId,
                snapshot.Status,
                snapshot.TerminalText);


    private async Task<ConversationHistoryMetadata> TryGetConversationHistoryMetadataAsync(
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            var store = new ConversationStore(_dataDirectory);
            return await store.GetHistoryMetadataAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ConversationHistoryMetadata(0, null, null, 0, []);
        }
    }

    private static string? ValidateResumedExpertPrompt(
        PersistedSessionSnapshot snapshot,
        ExpertModeOptions expertOptions)
    {
        if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt))
        {
            return "An empty Agent prompt cannot be used to resume an Expert Session.";
        }

        var agentSnapshot = snapshot.AgentSnapshots.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, expertOptions.Agent.AgentId, StringComparison.Ordinal));
        if (agentSnapshot is null)
        {
            return $"Agent '{expertOptions.Agent.AgentId}' is missing from the Session Agent snapshots.";
        }

        var promptHash = RuntimeRunMetadata.ComputePromptHash(expertOptions.Agent.SystemPrompt);
        if (!string.Equals(promptHash, agentSnapshot.PromptHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"Agent prompt hash for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.";
        }

        if (!string.Equals(
                expertOptions.Agent.PromptTemplateVersion,
                agentSnapshot.PromptTemplateVersion,
                StringComparison.Ordinal))
        {
            return $"Agent prompt template version for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.";
        }

        return null;
    }

    private static List<string> CreateRecoveryDiagnostics(PersistedSessionSnapshot snapshot)
    {
        var diagnostics = new List<string>
        {
            "Canonical message bodies and mode-specific checkpoints are not fully recoverable until phase 6."
        };
        if (snapshot.SelectionJson is null)
        {
            diagnostics.Add("The Session has no persisted Selection snapshot.");
        }

        if (snapshot.LatestRun is { Status: var status }
            && !Madorin.AI.Runtime.Core.RunStateMachine.IsTerminal(status))
        {
            diagnostics.Add("The latest Run has not reached a recoverable terminal state.");
        }

        return diagnostics;
    }

    private static NextTurnSelection? DeserializePersistedSelection(
        PersistedSessionSnapshot snapshot,
        List<string> diagnostics)
    {
        if (snapshot.SelectionJson is null)
        {
            return null;
        }

        try
        {
            var selection = JsonSerializer.Deserialize(
                snapshot.SelectionJson,
                RuntimeJsonContext.Default.NextTurnSelection);
            if (selection is null
                || selection.Mode != snapshot.Mode
                || selection.SelectionVersion != snapshot.SelectionVersion)
            {
                diagnostics.Add("The persisted Selection metadata is inconsistent.");
                return null;
            }

            return selection;
        }
        catch (JsonException)
        {
            diagnostics.Add("The persisted Selection snapshot is not valid JSON.");
            return null;
        }
    }

    internal static MeetingResumeState? BuildMeetingResumeState(
        MeetingSnapshot meetingSnapshot,
        PersistedSessionSnapshot sessionSnapshot,
        NextTurnSelection? selection,
        List<string> diagnostics)
    {
        var sessionRecord = meetingSnapshot.Session;

        if (!Enum.TryParse<MeetingSessionStatus>(
                sessionRecord.Status,
                ignoreCase: true,
                out var meetingStatus))
        {
            diagnostics.Add(
                $"The meeting session status '{sessionRecord.Status}' is not a recognized value.");
            return null;
        }

        var selectionParticipants = selection?.ModeOptions is MeetingModeOptions meeting
            ? meeting.Participants
            : [];

        var participants = new MeetingParticipant[meetingSnapshot.Participants.Count];
        for (var i = 0; i < meetingSnapshot.Participants.Count; i++)
        {
            var record = meetingSnapshot.Participants[i];

            if (!Enum.TryParse<ParticipantStatus>(
                    record.Status,
                    ignoreCase: true,
                    out var participantStatus))
            {
                diagnostics.Add(
                    $"The participant '{record.ParticipantId}' has an unrecognized status '{record.Status}'.");
                return null;
            }

            AgentRef? agentRef = null;
            if (!string.IsNullOrWhiteSpace(record.AgentRefJson))
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize(
                        record.AgentRefJson,
                        RuntimeJsonContext.Default.AgentRef);
                    if (deserialized is not null)
                    {
                        agentRef = deserialized with { SystemPrompt = string.Empty };
                    }
                }
                catch (JsonException)
                {
                    // Fall through to selection fallback below.
                }
            }

            if (agentRef is null)
            {
                var fallback = selectionParticipants.FirstOrDefault(
                    p => string.Equals(p.ParticipantId, record.ParticipantId, StringComparison.Ordinal));
                if (fallback is not null)
                {
                    agentRef = fallback.Agent with { SystemPrompt = string.Empty };
                }
                else
                {
                    diagnostics.Add(
                        $"The participant '{record.ParticipantId}' has no recoverable AgentRef.");
                    return null;
                }
            }

            participants[i] = new MeetingParticipant(
                record.ParticipantId,
                agentRef,
                record.DisplayName ?? record.ParticipantId,
                record.JoinOrder,
                participantStatus);
        }

        var persistedVersion = sessionRecord.SelectionVersion;
        if (sessionSnapshot.SelectionVersion.HasValue
            && persistedVersion != sessionSnapshot.SelectionVersion.Value)
        {
            diagnostics.Add(
                $"The meeting SelectionVersion ({persistedVersion}) does not match the session SelectionVersion ({sessionSnapshot.SelectionVersion.Value}).");
        }

        MeetingScheduledInvocation? nextInvocation = null;
        if (meetingSnapshot.NextScheduledInvocation is { } inv)
        {
            var invocationValid = true;

            if (!Enum.TryParse<MeetingInvocationRole>(
                    inv.Role,
                    ignoreCase: true,
                    out var invocationRole))
            {
                diagnostics.Add(
                    $"The next scheduled invocation '{inv.InvocationId}' has an unrecognized role '{inv.Role}'.");
                invocationValid = false;
            }

            if (!Enum.TryParse<MeetingInvocationStatus>(
                    inv.Status,
                    ignoreCase: true,
                    out var invocationStatus))
            {
                diagnostics.Add(
                    $"The next scheduled invocation '{inv.InvocationId}' has an unrecognized status '{inv.Status}'.");
                invocationValid = false;
            }

            if (invocationValid)
            {
                nextInvocation = new MeetingScheduledInvocation(
                    inv.InvocationId,
                    inv.RoundIndex,
                    invocationRole,
                    inv.ParticipantId,
                    inv.AgentId ?? string.Empty,
                    inv.SelectionVersion,
                    invocationStatus);
            }
        }

        MeetingHitlRequest? pendingApproval = null;
        if (string.Equals(
                meetingSnapshot.PendingApprovalStatus,
                "Pending",
                StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(meetingSnapshot.PendingApprovalJson))
            {
                diagnostics.Add(
                    "The pending meeting approval status is Pending but the approval JSON is empty.");
            }
            else
            {
                try
                {
                    pendingApproval = JsonSerializer.Deserialize(
                        meetingSnapshot.PendingApprovalJson,
                        RuntimeJsonContext.Default.MeetingHitlRequest);
                    if (pendingApproval is null)
                    {
                        diagnostics.Add(
                            "The pending meeting approval JSON deserialized to null.");
                    }
                }
                catch (JsonException)
                {
                    diagnostics.Add(
                        "The pending meeting approval JSON is not valid.");
                }
            }
        }

        MeetingSummary? recentSummary = null;
        if (meetingSnapshot.LatestSummary is { } summary)
        {
            if (!string.IsNullOrWhiteSpace(summary.SummaryMessageId)
                && summary.SummarizesThroughSeq.HasValue
                && !string.IsNullOrWhiteSpace(summary.SummaryPolicyHash)
                && !string.IsNullOrWhiteSpace(summary.SummaryAgentId)
                && !string.IsNullOrWhiteSpace(summary.SummaryInvocationId))
            {
                recentSummary = new MeetingSummary(
                    summary.SummaryMessageId,
                    summary.RoundIndex,
                    summary.SummarizesThroughSeq.Value,
                    summary.SummaryPolicyHash,
                    summary.SummaryAgentId,
                    summary.SummaryInvocationId);
            }
            else
            {
                diagnostics.Add(
                    "The latest meeting summary is incomplete.");
            }
        }

        return new MeetingResumeState(
            meetingStatus,
            sessionRecord.CurrentRound,
            participants,
            persistedVersion,
            nextInvocation,
            pendingApproval,
            recentSummary);
    }

    internal static NeededAgentDefinition[] CreateNeededDefinitions(
        PersistedSessionSnapshot snapshot,
        NextTurnSelection? selection)
    {
        var roles = new Dictionary<string, (string Type, string? ParticipantId)>(
            StringComparer.Ordinal);
        switch (selection?.ModeOptions)
        {
            case ExpertModeOptions expert:
                roles[expert.Agent.AgentId] = ("SingleAgent", null);
                break;
            case MeetingModeOptions meeting:
                foreach (var participant in meeting.Participants)
                {
                    roles.TryAdd(participant.Agent.AgentId, ("MeetingParticipant", participant.ParticipantId));
                }

                if (meeting.HostAgent is not null)
                {
                    roles.TryAdd(meeting.HostAgent.AgentId, ("MeetingHost", null));
                }

                if (meeting.SelectorPolicy?.SelectorAgent is not null)
                {
                    roles.TryAdd(meeting.SelectorPolicy.SelectorAgent.AgentId, ("MeetingSelector", null));
                }

                if (meeting.Policy?.Summarizer is not null)
                {
                    roles.TryAdd(meeting.Policy.Summarizer.AgentId, ("MeetingSummarizer", null));
                }

                break;
            case WorkModeOptions work:
                roles[work.GeneralManager.AgentId] = ("WorkGeneralManager", null);
                foreach (var agent in work.AvailableAgents)
                {
                    roles[agent.AgentId] = ("WorkSubAgent", null);
                }

                break;
        }

        return
        [
            .. snapshot.AgentSnapshots.Select(agent =>
            {
                var role = roles.GetValueOrDefault(
                    agent.AgentId,
                    snapshot.Mode switch
                    {
                        RuntimeMode.Expert => ("SingleAgent", null),
                        RuntimeMode.Meeting => ("MeetingParticipant", agent.AgentId),
                        RuntimeMode.Work => ("WorkSubAgent", null),
                        _ => ("Agent", null)
                    });
                return new NeededAgentDefinition(
                    agent.AgentId,
                    agent.PromptHash,
                    role.Item1,
                    role.Item2);
            })
        ];
    }

    private static NextTurnSelection RehydrateSelection(
        NextTurnSelection selection,
        Dictionary<string, AgentDefinition> definitions)
    {
        AgentRef RehydrateAgent(AgentRef persisted)
        {
            var definition = definitions[persisted.AgentId];
            return persisted with { SystemPrompt = definition.AgentRef.SystemPrompt };
        }

        ModeOptions modeOptions = selection.ModeOptions switch
        {
            ExpertModeOptions expert => new ExpertModeOptions(RehydrateAgent(expert.Agent)),
            MeetingModeOptions meeting => meeting with
            {
                Participants = [.. meeting.Participants.Select(
                    participant => participant with { Agent = RehydrateAgent(participant.Agent) })],
                HostAgent = meeting.HostAgent is not null ? RehydrateAgent(meeting.HostAgent) : null,
                SelectorPolicy = meeting.SelectorPolicy is { SelectorAgent: not null }
                    ? meeting.SelectorPolicy with { SelectorAgent = RehydrateAgent(meeting.SelectorPolicy.SelectorAgent) }
                    : meeting.SelectorPolicy,
                Policy = meeting.Policy is { Summarizer: not null }
                    ? meeting.Policy with { Summarizer = RehydrateAgent(meeting.Policy.Summarizer) }
                    : meeting.Policy,
            },
            WorkModeOptions work => new WorkModeOptions(
                RehydrateAgent(work.GeneralManager),
                [.. work.AvailableAgents.Select(RehydrateAgent)],
                work.WorkflowPolicy,
                work.ContextPolicy),
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };
        return selection with { ModeOptions = modeOptions };
    }

    private async Task WritePidFileAsync(string path, CancellationToken ct)
    {
        var info = new RuntimePidInfo(InstanceId, PipeName, EventPipeName, Environment.ProcessId, DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            info,
            RuntimePidInfoContext.Default.RuntimePidInfo);
        await File.WriteAllBytesAsync(path, json, ct).ConfigureAwait(false);
    }

    private static void TryDeletePidFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private static byte[] DecodeNonce(string value)
    {
        try
        {
            var nonce = Convert.FromBase64String(value);
            return nonce.Length == 32
                ? nonce
                : throw new InvalidDataException("The handshake nonce must contain exactly 32 bytes.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The handshake nonce is not valid Base64.", ex);
        }
    }

    private static void ValidateDirectoryLayout(
        string dataDirectory,
        string configDirectory,
        string logDirectory)
    {
        var directories = new[] { dataDirectory, configDirectory, logDirectory };
        for (var index = 0; index < directories.Length; index++)
        {
            for (var otherIndex = index + 1; otherIndex < directories.Length; otherIndex++)
            {
                if (string.Equals(
                        directories[index],
                        directories[otherIndex],
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new ArgumentException("Data, configuration, and log directories must be distinct.");
                }
            }
        }
    }

    private void PruneSeenNonces(long nowUnixMilliseconds)
    {
        var cutoff = nowUnixMilliseconds - (long)Math.Max(
            _handshakeClockSkew.TotalMilliseconds * 2,
            TimeSpan.FromMinutes(2).TotalMilliseconds);
        foreach (var pair in _seenClientNonces)
        {
            if (pair.Value < cutoff)
            {
                _seenClientNonces.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task MonitorHostLeasesAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_hostLeaseTimeout.TotalMilliseconds / 3, 50, 1000));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();
                foreach (var pair in _sessions.ToArray())
                {
                    if (!pair.Value.IsExpired(now, _hostLeaseTimeout)
                        || !_sessions.TryRemove(pair.Key, out var expiredSession))
                    {
                        continue;
                    }

                    foreach (var run in _activeRuns.Values.Where(candidate =>
                                 string.Equals(
                                     candidate.OwnerHostInstanceId,
                                     expiredSession.HostInstanceId,
                                     StringComparison.Ordinal)))
                    {
                        run.Cancel();
                    }

                    await _blobStore.AbortSessionAsync(
                        expiredSession.HostInstanceId,
                        CancellationToken.None).ConfigureAwait(false);
                    var eventChannel = expiredSession.TryGetEventChannel();
                    if (eventChannel is not null)
                    {
                        expiredSession.DetachEventChannel(eventChannel);
                        await eventChannel.DisposeAsync().ConfigureAwait(false);
                    }

                    await expiredSession.DisconnectControlChannelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task DrainConnectionsAsync()
    {
        var tasks = _connections.Values.ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task DrainRunsAsync()
    {
        var tasks = _activeRuns.Values
            .Select(static run => run.Completion)
            .ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private sealed class ActiveRun(
        RunRegistry.RunLease lease,
        string ownerHostInstanceId,
        IRuntimeProviderAdapter provider) : IDisposable
    {
        private readonly RunRegistry.RunLease _lease = lease
            ?? throw new ArgumentNullException(nameof(lease));
        private readonly IRuntimeProviderAdapter _provider = provider
            ?? throw new ArgumentNullException(nameof(provider));
        private readonly object _credentialGate = new();
        private TaskCompletionSource<bool>? _credentialWaiter;
        private readonly object _meetingHitlGate = new();
        private string? _meetingHitlApprovalRequestId;
        private TaskCompletionSource<MeetingHitlResponse>? _meetingHitlWaiter;

        private static readonly TimeSpan CredentialWaitTimeout = TimeSpan.FromSeconds(30);

        public CancellationToken CancellationToken => _lease.CancellationToken;

        public bool IsTimedOut => _lease.IsTimedOut;

        public string OwnerHostInstanceId { get; } = ownerHostInstanceId;

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Attach(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            Completion = completion;
        }

        public bool Cancel() => _lease.Cancel();

        public void ReleaseCapacity() => _lease.Dispose();

        public async Task<bool> WaitForCredentialsAsync(
            string providerId,
            CancellationToken ct)
        {
            if (!string.Equals(_provider.ProviderId, providerId, StringComparison.Ordinal)
                || _provider is not IRuntimeProviderCredentialUpdater)
            {
                return false;
            }

            var waiter = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_credentialGate)
            {
                if (_credentialWaiter is not null)
                {
                    return false;
                }

                _credentialWaiter = waiter;
            }

            try
            {
                return await waiter.Task.WaitAsync(CredentialWaitTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            finally
            {
                lock (_credentialGate)
                {
                    if (ReferenceEquals(_credentialWaiter, waiter))
                    {
                        _credentialWaiter = null;
                    }
                }
            }
        }

        public async Task<bool> UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct)
        {
            TaskCompletionSource<bool>? waiter;
            lock (_credentialGate)
            {
                waiter = _credentialWaiter;
            }

            if (waiter is null
                || !string.Equals(_provider.ProviderId, parameters.ProviderId, StringComparison.Ordinal)
                || _provider is not IRuntimeProviderCredentialUpdater updater)
            {
                return false;
            }

            await updater.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false);
            waiter.TrySetResult(true);
            return true;
        }

        public void CancelCredentialWait()
        {
            lock (_credentialGate)
            {
                _credentialWaiter?.TrySetResult(false);
                _credentialWaiter = null;
            }
        }

        public async Task<MeetingHitlResponse> WaitForMeetingHitlAsync(
            MeetingHitlRequest request,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var waiter = new TaskCompletionSource<MeetingHitlResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_meetingHitlGate)
            {
                if (_meetingHitlWaiter is not null)
                {
                    throw new InvalidOperationException(
                        "A meeting HITL waiter is already registered for this Run.");
                }

                _meetingHitlApprovalRequestId = request.ApprovalRequestId;
                _meetingHitlWaiter = waiter;
            }

            try
            {
                if (request.ExpiresAt is { } expiresAt)
                {
                    var remaining = expiresAt - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException();
                    }

                    return await waiter.Task.WaitAsync(remaining, ct)
                        .ConfigureAwait(false);
                }

                return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                CancelMeetingHitlWait();
                throw;
            }
        }

        public bool TryCompleteMeetingHitl(MeetingHitlResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            TaskCompletionSource<MeetingHitlResponse>? waiter;
            string? expectedId;
            lock (_meetingHitlGate)
            {
                waiter = _meetingHitlWaiter;
                expectedId = _meetingHitlApprovalRequestId;
                if (waiter is null)
                {
                    return false;
                }

                if (!string.Equals(
                    expectedId,
                    response.ApprovalRequestId,
                    StringComparison.Ordinal))
                {
                    return false;
                }

                _meetingHitlWaiter = null;
                _meetingHitlApprovalRequestId = null;
            }

            return waiter.TrySetResult(response);
        }

        public bool HasMeetingHitlWaiter(string approvalRequestId)
        {
            lock (_meetingHitlGate)
            {
                return _meetingHitlWaiter is not null
                    && string.Equals(
                        _meetingHitlApprovalRequestId,
                        approvalRequestId,
                        StringComparison.Ordinal);
            }
        }

        public void CancelMeetingHitlWait()
        {
            lock (_meetingHitlGate)
            {
                _meetingHitlWaiter?.TrySetCanceled();
                _meetingHitlWaiter = null;
                _meetingHitlApprovalRequestId = null;
            }
        }

        public void Dispose()
        {
            CancelCredentialWait();
            CancelMeetingHitlWait();
            _lease.Dispose();
        }
    }

    private sealed record ActiveRunRequest(
        string SessionIdentity,
        string RequestHash,
        RunAcceptedEvent Accepted);

    private sealed record RunStartPlan(
        string SessionIdentity,
        string RequestHash,
        NewSessionRunRequest ExecutionRequest,
        string? ExistingSessionId,
        string? SessionIdempotencyKey,
        NextTurnSelection? InitialSelection);

    private sealed record RecoveredMeetingRunStart(
        string SessionId,
        string RunId,
        NewSessionRunRequest Request,
        IRuntimeProviderAdapter Provider,
        Func<string, IRuntimeProviderAdapter> ProviderFactory,
        RunRegistry.RunLease Lease,
        MeetingInvocationRecord? ResumedInvocation,
        ConversationRecordV1? ResumedCanonicalMessage = null,
        RuntimeError? ResumedFailure = null);

    private sealed class AuthenticatedSession(
        string hostInstanceId,
        string sessionKey,
        NamedPipeTransport controlTransport,
        DateTimeOffset lastActivity) : IDisposable
    {
        private readonly ToolCatalogStore _toolCatalogStore = new(
            new BuiltinToolRegistry(),
            new JsonSchemaToolValidator());
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly object _eventChannelGate = new();
        private TaskCompletionSource<FramedEventChannel> _eventChannelSource = CreateEventChannelSource();
        private IDuplexRpcPeer? _controlPeer;
        private RuntimeCapabilities? _hostCapabilities;
        private string? _protocolVersion;
        private long _lastConfirmedGsn;
        private long _lastDispatchedGsn;
        private int _initialized;
        private long _lastActivityUnixMilliseconds = lastActivity.ToUnixTimeMilliseconds();

        public string HostInstanceId { get; } = hostInstanceId;

        public string SessionKey { get; } = sessionKey;

        public ToolCatalogStore ToolCatalogStore => _toolCatalogStore;

        public bool SupportsReverseRpc =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
            && _hostCapabilities?.ReverseRpc is true;

        public bool SupportsToolCatalog =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
            && _hostCapabilities?.ToolCatalog is not false;

        public bool SupportsToolPermissions =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
            && _hostCapabilities?.ToolPermissions is not false;

        public ValueTask DisconnectControlChannelAsync() => controlTransport.DisposeAsync();

        public void AttachControlPeer(IDuplexRpcPeer controlPeer)
        {
            ArgumentNullException.ThrowIfNull(controlPeer);
            if (Interlocked.CompareExchange(ref _controlPeer, controlPeer, null) is not null)
            {
                throw new InvalidOperationException("The control peer is already attached.");
            }
        }

        public IDuplexRpcPeer? TryGetControlPeer() => Volatile.Read(ref _controlPeer);

        public Task EnterRequestAsync(CancellationToken ct) => _requestGate.WaitAsync(ct);

        public void ExitRequest() => _requestGate.Release();

        public void Dispose() => _requestGate.Dispose();

        public void Touch(DateTimeOffset timestamp) =>
            Interlocked.Exchange(
                ref _lastActivityUnixMilliseconds,
                timestamp.ToUnixTimeMilliseconds());

        public bool IsExpired(DateTimeOffset now, TimeSpan timeout) =>
            now.ToUnixTimeMilliseconds()
                - Interlocked.Read(ref _lastActivityUnixMilliseconds)
                >= timeout.TotalMilliseconds;

        public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

        public long LastConfirmedGsn => Interlocked.Read(ref _lastConfirmedGsn);

        public long LastDispatchedGsn => Interlocked.Read(ref _lastDispatchedGsn);

        public void Initialize(
            long lastConfirmedGsn,
            string protocolVersion,
            RuntimeCapabilities hostCapabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
            ArgumentNullException.ThrowIfNull(hostCapabilities);
            _protocolVersion = protocolVersion;
            _hostCapabilities = hostCapabilities;
            Confirm(lastConfirmedGsn);
            MarkDispatched(lastConfirmedGsn);
            Volatile.Write(ref _initialized, 1);
        }

        public void Confirm(long lastConfirmedGsn)
        {
            var current = Interlocked.Read(ref _lastConfirmedGsn);
            while (lastConfirmedGsn > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _lastConfirmedGsn,
                    lastConfirmedGsn,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        public void MarkDispatched(long lastDispatchedGsn)
        {
            var current = Interlocked.Read(ref _lastDispatchedGsn);
            while (lastDispatchedGsn > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _lastDispatchedGsn,
                    lastDispatchedGsn,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        public Task<FramedEventChannel> WaitForEventChannelAsync(CancellationToken ct)
        {
            Task<FramedEventChannel> channelTask;
            lock (_eventChannelGate)
            {
                channelTask = _eventChannelSource.Task;
            }

            return channelTask.WaitAsync(ct);
        }

        public bool TryAttachEventChannel(FramedEventChannel eventChannel)
        {
            lock (_eventChannelGate)
            {
                return _eventChannelSource.TrySetResult(eventChannel);
            }
        }

        public FramedEventChannel? TryGetEventChannel()
        {
            lock (_eventChannelGate)
            {
                return _eventChannelSource.Task.IsCompletedSuccessfully
                    ? _eventChannelSource.Task.Result
                    : null;
            }
        }

        public void DetachEventChannel(FramedEventChannel eventChannel)
        {
            lock (_eventChannelGate)
            {
                if (_eventChannelSource.Task.IsCompletedSuccessfully
                    && ReferenceEquals(_eventChannelSource.Task.Result, eventChannel))
                {
                    _eventChannelSource = CreateEventChannelSource();
                }
            }
        }

        private static TaskCompletionSource<FramedEventChannel> CreateEventChannelSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class UnavailableProviderAdapter(string providerId) : IRuntimeProviderAdapter
    {
        public string ProviderId { get; } = providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                new RuntimeError(
                    "ProviderNotConfigured",
                    "provider",
                    $"Provider '{ProviderId}' is not configured for this Runtime instance.",
                    IsRetryable: false,
                    ProviderDetails: null,
                    Guid.NewGuid().ToString("N")));
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
