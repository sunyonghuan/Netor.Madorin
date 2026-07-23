using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.Abstractions;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.Client;

public sealed class RuntimeClient : IRuntimeClient
{
    private readonly RuntimeClientOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private NamedPipeTransport? _transport;
    private NamedPipeTransport? _eventTransport;
    private AuthenticatedFrameChannel? _authenticatedControl;
    private FramedControlChannel? _controlChannel;
    private FramedEventChannel? _eventChannel;
    private FramedBlobChannel? _blobChannel;
    private RuntimeLimits _limits = new();
    private string? _runtimeInstanceId;
    private string? _confirmedRuntimeInstanceId;
    private string? _sessionKey;
    private string? _eventPipeName;
    private long _lastConfirmedGsn;
    private CancellationTokenSource? _lifecycleCancellation;
    private Task? _heartbeatTask;
    private int _heartbeatIntervalSeconds;
    private int _disposed;

    public RuntimeClient(RuntimeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientVersion);
        if (options.ConnectTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ConnectTimeout,
                "Connect timeout cannot be negative.");
        }

        if (options.ExpectedServerProcessId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ExpectedServerProcessId,
                "The expected server process identifier must be positive.");
        }

        if (options.HeartbeatInterval < TimeSpan.Zero
            || options.ReconnectWindow < TimeSpan.Zero
            || options.ReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Heartbeat and reconnect intervals cannot be negative.");
        }

        _options = options;
        State = RuntimeClientState.Starting;
    }

    public RuntimeClientState State { get; private set; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? LastSentAt => _controlChannel?.LastSentAt;

    public DateTimeOffset? LastReceivedAt => _controlChannel?.LastReceivedAt;

    public IBlobChannel BlobChannel =>
        State == RuntimeClientState.Connected && _blobChannel is not null
            ? _blobChannel
            : throw new InvalidOperationException("The Runtime Blob channel is not connected.");

    public IDuplexRpcPeer ControlPeer =>
        State == RuntimeClientState.Connected && _controlChannel is not null
            ? _controlChannel
            : throw new InvalidOperationException("The Runtime control channel is not connected.");

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RuntimeClientState.Connected)
            {
                return;
            }

            await ConnectCoreAsync(startHeartbeat: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            State = RuntimeClientState.Reconnecting;
            await DisposeTransportAsync().ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow + _options.ResolvedReconnectWindow;
            Exception? lastError = null;
            while (!cancellationToken.IsCancellationRequested
                && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await ConnectCoreAsync(startHeartbeat: false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or TimeoutException)
                {
                    lastError = ex;
                    ConsecutiveFailures++;
                    await Task.Delay(_options.ResolvedReconnectDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            State = RuntimeClientState.Faulted;
            throw new TimeoutException(
                $"The Runtime reconnect window expired. Last error: {lastError?.Message}");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async ValueTask ConnectCoreAsync(
        bool startHeartbeat,
        CancellationToken cancellationToken)
    {
        State = RuntimeClientState.Connecting;
        using var connectTimeout = CreateConnectTimeoutSource(
            _options.ConnectTimeout,
            cancellationToken);
        var connectToken = connectTimeout?.Token ?? cancellationToken;
        try
        {
            _transport = await NamedPipeTransport.ConnectAsync(
                _options.PipeName,
                connectToken).ConfigureAwait(false);
            ValidatePeerProcess(_transport);
            State = RuntimeClientState.Authenticating;
            await AuthenticateAsync(_transport, connectToken).ConfigureAwait(false);
            _authenticatedControl = new AuthenticatedFrameChannel(
                _transport,
                _sessionKey!,
                "host-control",
                "runtime-control");
            _controlChannel = new FramedControlChannel(_authenticatedControl);
            State = RuntimeClientState.Initializing;
            var initializeResponse = await InitializeAsync(connectToken).ConfigureAwait(false);
            if (!initializeResponse.Capabilities.BlobTransfer)
            {
                throw new InvalidDataException("The Runtime did not negotiate Blob transfer support.");
            }

            _heartbeatIntervalSeconds = initializeResponse.HeartbeatIntervalSeconds;
            _limits = initializeResponse.Limits;
            _blobChannel = new FramedBlobChannel(_controlChannel);
            if (!string.Equals(
                _confirmedRuntimeInstanceId,
                _runtimeInstanceId,
                StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _lastConfirmedGsn, 0);
            }

            _confirmedRuntimeInstanceId = _runtimeInstanceId;
            _eventPipeName = _options.EventPipeName ?? initializeResponse.EventChannelAddress;
            await ConnectEventChannelAsync(_eventPipeName, connectToken).ConfigureAwait(false);
            LastConnectedAt = DateTimeOffset.UtcNow;
            ConsecutiveFailures = 0;
            State = RuntimeClientState.Connected;
            if (startHeartbeat && _options.EnableBackgroundHeartbeat)
            {
                _lifecycleCancellation ??= new CancellationTokenSource();
                _heartbeatTask ??= Task.Run(
                    () => HeartbeatLoopAsync(_lifecycleCancellation.Token),
                    CancellationToken.None);
            }
        }
        catch
        {
            ConsecutiveFailures++;
            State = RuntimeClientState.Faulted;
            await DisposeTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eventReplayCursor);
        if (State != RuntimeClientState.Connected || _eventChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var replayCursor = Math.Max(
            eventReplayCursor,
            Interlocked.Read(ref _lastConfirmedGsn));
        while (!cancellationToken.IsCancellationRequested)
        {
            var eventChannel = _eventChannel
                ?? throw new InvalidOperationException("The Runtime event channel is not connected.");
            await using var enumerator = eventChannel
                .ReadAllAsync(replayCursor, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            RuntimeEventEnvelope envelope;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    await ReconnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                envelope = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException
                or EndOfStreamException
                or ObjectDisposedException)
            {
                await ReconnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(
                envelope.RuntimeInstanceId,
                _runtimeInstanceId,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The event belongs to a different Runtime instance.");
            }

            if (envelope.Gsn <= Interlocked.Read(ref _lastConfirmedGsn))
            {
                continue;
            }

            yield return envelope;
            await eventChannel.AcknowledgeAsync(envelope.Gsn, cancellationToken)
                .ConfigureAwait(false);
            replayCursor = envelope.Gsn;
        }
    }

    public async ValueTask<string> StartNewSessionRunAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var normalizedRequest = await MoveOversizedInlineContentAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        var parameters = JsonSerializer.SerializeToElement(
            normalizedRequest,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        var response = await _controlChannel.SendRequestAsync(
            MessageTypes.NewSessionRun,
            parameters,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException("The run.new_session response did not contain a result.");
        var accepted = JsonSerializer.Deserialize(result, RuntimeJsonContext.Default.RunAcceptedEvent)
            ?? throw new InvalidDataException("The run.new_session response was empty.");
        return accepted.RunId;
    }

    public async ValueTask<string> StartExistingSessionRunAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var normalizedRequest = await MoveOversizedInlineContentAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        var parameters = JsonSerializer.SerializeToElement(
            normalizedRequest,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        var response = await _controlChannel.SendRequestAsync(
            MessageTypes.ExistingSessionRun,
            parameters,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException(
                "The run.existing_session response did not contain a result.");
        var accepted = JsonSerializer.Deserialize(
            result,
            RuntimeJsonContext.Default.RunAcceptedEvent)
            ?? throw new InvalidDataException("The run.existing_session response was empty.");
        return accepted.RunId;
    }

    private async ValueTask<NewSessionRunRequest> MoveOversizedInlineContentAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken)
    {
        var blobChannel = _blobChannel
            ?? throw new InvalidOperationException("The Runtime Blob channel is not connected.");
        var changed = false;
        var content = new ContentBlock[request.InitialInput.Length];
        for (var index = 0; index < request.InitialInput.Length; index++)
        {
            content[index] = await MoveOversizedInlineContentAsync(
                request.InitialInput[index],
                request.RunIdempotencyKey,
                blobChannel,
                cancellationToken).ConfigureAwait(false);
            changed |= !ReferenceEquals(content[index], request.InitialInput[index]);
        }

        return changed ? request with { InitialInput = content } : request;
    }

    private async ValueTask<ExistingSessionRunRequest> MoveOversizedInlineContentAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputOverride is not { } input)
        {
            return request;
        }

        var blobChannel = _blobChannel
            ?? throw new InvalidOperationException("The Runtime Blob channel is not connected.");
        var changed = false;
        var content = new ContentBlock[input.Length];
        for (var index = 0; index < input.Length; index++)
        {
            content[index] = await MoveOversizedInlineContentAsync(
                input[index],
                request.RunIdempotencyKey,
                blobChannel,
                cancellationToken).ConfigureAwait(false);
            changed |= !ReferenceEquals(content[index], input[index]);
        }

        return changed ? request with { InputOverride = content } : request;
    }

    private async ValueTask<ContentBlock> MoveOversizedInlineContentAsync(
        ContentBlock content,
        string runId,
        FramedBlobChannel blobChannel,
        CancellationToken cancellationToken)
    {
        string? text = content switch
        {
            TextContentBlock block => block.Text,
            ReasoningContentBlock block => block.Content,
            _ => null
        };
        if (text is not null && Encoding.UTF8.GetByteCount(text) > _limits.MaxInlineContentBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await using var stream = new MemoryStream(bytes, writable: false);
            var reference = await blobChannel.WriteReferenceAsync(
                stream,
                "text/plain; charset=utf-8",
                runId,
                "run",
                TimeSpan.FromHours(1),
                cancellationToken).ConfigureAwait(false);
            return new BlobRefContentBlock(reference);
        }

        if (content is ToolResultContentBlock toolResult)
        {
            var changed = false;
            var nested = new ContentBlock[toolResult.Content.Length];
            for (var index = 0; index < toolResult.Content.Length; index++)
            {
                nested[index] = await MoveOversizedInlineContentAsync(
                    toolResult.Content[index],
                    runId,
                    blobChannel,
                    cancellationToken).ConfigureAwait(false);
                changed |= !ReferenceEquals(nested[index], toolResult.Content[index]);
            }

            return changed ? toolResult with { Content = nested } : toolResult;
        }

        return content;
    }

    public async ValueTask AcknowledgeEventsAsync(
        long lastConfirmedGsn,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastConfirmedGsn);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        if (lastConfirmedGsn <= Interlocked.Read(ref _lastConfirmedGsn))
        {
            return;
        }

        var parameters = JsonSerializer.SerializeToElement(
            new EventAcknowledgeParameters(lastConfirmedGsn),
            RuntimeJsonContext.Default.EventAcknowledgeParameters);
        var response = await _controlChannel.SendRequestAsync(
            "events.acknowledge",
            parameters,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        UpdateConfirmedGsn(lastConfirmedGsn);
        _confirmedRuntimeInstanceId = _runtimeInstanceId;
    }

    public async ValueTask<bool> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var runIdElement = System.Text.Json.JsonSerializer.SerializeToElement(
            new RunCancelParameters(runId),
            RuntimeJsonContext.Default.RunCancelParameters);
        var response = await _controlChannel.SendRequestAsync(
            "run.cancel",
            runIdElement,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException("The run.cancel response did not contain a result.");
        return JsonSerializer.Deserialize(result, RuntimeJsonContext.Default.Boolean);
    }

    public async ValueTask<RunQueryResult> QueryRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var parameters = JsonSerializer.SerializeToElement(
            new RunQueryParameters(runId),
            RuntimeJsonContext.Default.RunQueryParameters);
        var response = await _controlChannel.SendRequestAsync(
            MessageTypes.RunQuery,
            parameters,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException("The run.query response did not contain a result.");
        return JsonSerializer.Deserialize(result, RuntimeJsonContext.Default.RunQueryResult)
            ?? throw new InvalidDataException("The run.query response was empty.");
    }

    public async ValueTask<SessionGetResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await SendSessionRequestAsync(
            MessageTypes.SessionGet,
            new SessionIdParameters(sessionId),
            RuntimeJsonContext.Default.SessionGetResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionResumeResult> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await SendSessionRequestAsync(
            MessageTypes.SessionResume,
            new SessionIdParameters(sessionId),
            RuntimeJsonContext.Default.SessionResumeResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await SendSessionRequestAsync(
            MessageTypes.SessionMessagesList,
            parameters,
            RuntimeJsonContext.Default.SessionMessagesListResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NextTurnSelection> UpdateSessionSelectionAsync(
        SessionSelectionUpdateParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await SendSessionRequestAsync(
            MessageTypes.SessionSelectionUpdate,
            parameters,
            RuntimeJsonContext.Default.NextTurnSelection,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionRehydrateResult> RehydrateSessionAsync(
        SessionRehydrateParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await SendSessionRequestAsync(
            MessageTypes.SessionRehydrate,
            parameters,
            RuntimeJsonContext.Default.SessionRehydrateResult,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TResult> SendSessionRequestAsync<TParameters, TResult>(
        string method,
        TParameters parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
        where TParameters : notnull
    {
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var parameterElement = JsonSerializer.SerializeToElement(
            parameters,
            typeof(TParameters),
            RuntimeJsonContext.Default);
        var response = await _controlChannel.SendRequestAsync(
            method,
            parameterElement,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException($"The {method} response did not contain a result.");
        return JsonSerializer.Deserialize(result, resultType)
            ?? throw new InvalidDataException($"The {method} response was empty.");
    }

    public async ValueTask UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.Credential);
        if (State != RuntimeClientState.Connected || _controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var payload = JsonSerializer.SerializeToElement(
            parameters,
            RuntimeJsonContext.Default.CredentialsUpdateParameters);
        var response = await _controlChannel.SendRequestAsync(
            MessageTypes.CredentialsUpdate,
            payload,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
    }

    public async ValueTask HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var controlChannel = _controlChannel;
        if (State != RuntimeClientState.Connected || controlChannel is null)
        {
            throw new InvalidOperationException("The Runtime client is not connected.");
        }

        var response = await controlChannel.SendRequestAsync(
            "heartbeat",
            ct: cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_lifecycleCancellation is not null)
        {
            await _lifecycleCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeTransportAsync().ConfigureAwait(false);
            State = RuntimeClientState.Closed;
        }
        finally
        {
            _stateGate.Release();
            _stateGate.Dispose();
            _lifecycleCancellation?.Dispose();
            _lifecycleCancellation = null;
            _heartbeatTask = null;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.HeartbeatInterval > TimeSpan.Zero
            ? _options.HeartbeatInterval
            : TimeSpan.FromSeconds(Math.Max(1, _heartbeatIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await HeartbeatAsync(cancellationToken).ConfigureAwait(false);
                ConsecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException
                or InvalidDataException
                or InvalidOperationException
                or ObjectDisposedException)
            {
                ConsecutiveFailures++;
                try
                {
                    await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception reconnectError) when (reconnectError is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or TimeoutException)
                {
                    State = RuntimeClientState.Faulted;
                }
            }
        }
    }

    private async ValueTask ConnectEventChannelAsync(
        string eventPipeName,
        CancellationToken cancellationToken)
    {
        _eventTransport = await NamedPipeTransport.ConnectAsync(
            eventPipeName,
            NamedPipeTransport.MaxEventMessageBytes,
            cancellationToken).ConfigureAwait(false);
        ValidatePeerProcess(_eventTransport);
        var eventHandshake = new FramedChannel(_eventTransport);
        await eventHandshake.SendJsonAsync(
            new EventChannelHello(
                _options.HostInstanceId,
                HandshakeProtocol.ComputeSessionProof(
                    _sessionKey!,
                    "event-channel",
                    _options.HostInstanceId,
                    _runtimeInstanceId!)),
            cancellationToken).ConfigureAwait(false);
        _eventChannel = new FramedEventChannel(
            _eventTransport,
            AcknowledgeEventsAsync,
            outboundCapacity: 0);
    }

    private async ValueTask ReconnectEventChannelAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RuntimeClientState.Connected
                || _sessionKey is null
                || _runtimeInstanceId is null
                || _eventPipeName is null)
            {
                throw new InvalidOperationException(
                    "The control channel is not available for event reconnection.");
            }

            if (_eventChannel is not null)
            {
                await _eventChannel.DisposeAsync().ConfigureAwait(false);
                _eventChannel = null;
                _eventTransport = null;
            }

            var deadline = DateTimeOffset.UtcNow + _options.ResolvedReconnectWindow;
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await ConnectEventChannelAsync(_eventPipeName, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException
                    or InvalidDataException
                    or TimeoutException)
                {
                    lastError = ex;
                    await Task.Delay(_options.ResolvedReconnectDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            throw new TimeoutException(
                $"The event channel reconnect window expired. Last error: {lastError?.Message}");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async ValueTask DisposeTransportAsync()
    {
        if (_eventChannel is not null)
        {
            await _eventChannel.DisposeAsync().ConfigureAwait(false);
            _eventChannel = null;
            _eventTransport = null;
        }
        else if (_eventTransport is not null)
        {
            await _eventTransport.DisposeAsync().ConfigureAwait(false);
            _eventTransport = null;
        }

        if (_controlChannel is not null)
        {
            await _controlChannel.DisposeAsync().ConfigureAwait(false);
            _controlChannel = null;
        }

        _blobChannel = null;

        _authenticatedControl?.Dispose();
        _authenticatedControl = null;
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        _runtimeInstanceId = null;
        _sessionKey = null;
    }

    private async ValueTask AuthenticateAsync(
        NamedPipeTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.HostInstanceId);
        var secret = _options.HandshakeSecret
            ?? throw new InvalidOperationException(
                "A handshake secret must be supplied through a controlled handle.");
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var nonceC = HandshakeProtocol.CreateNonce();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var channel = new FramedChannel(transport);
        await channel.SendJsonAsync(
            new HandshakeClientHello(
                _options.HostInstanceId,
                Convert.ToBase64String(nonceC),
                timestamp),
            cancellationToken).ConfigureAwait(false);
        var hello = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeServerHello,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The Runtime closed during authentication.");
        if (_options.ExpectedRuntimeInstanceId is not null
            && !string.Equals(
                hello.RuntimeInstanceId,
                _options.ExpectedRuntimeInstanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Runtime instance identifier did not match the expected process.");
        }

        var nonceS = DecodeNonce(hello.NonceS);
        if (!HandshakeProtocol.VerifyRuntimeResponse(
            secret,
            hello.RuntimeInstanceId,
            timestamp,
            nonceC,
            nonceS,
            hello.RuntimeResponse))
        {
            throw new InvalidDataException("The Runtime handshake response is invalid.");
        }

        _runtimeInstanceId = hello.RuntimeInstanceId;
        _sessionKey = HandshakeProtocol.DeriveSessionKey(
            secret,
            _options.HostInstanceId,
            hello.RuntimeInstanceId,
            nonceC,
            nonceS);
        await channel.SendJsonAsync(
            new HandshakeClientConfirmation(
                _options.HostInstanceId,
                HandshakeProtocol.ComputeSessionProof(
                    _sessionKey,
                    "control-channel",
                    _options.HostInstanceId,
                    hello.RuntimeInstanceId)),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<InitializeResponse> InitializeAsync(
        CancellationToken cancellationToken)
    {
        var request = new InitializeRequest(
            _options.HostInstanceId,
            _options.ClientVersion,
            ProtocolVersions.Supported,
            _options.Capabilities ?? new RuntimeCapabilities(),
            string.Equals(
                _confirmedRuntimeInstanceId,
                _runtimeInstanceId,
                StringComparison.Ordinal)
                ? Interlocked.Read(ref _lastConfirmedGsn)
                : 0);
        var parameters = JsonSerializer.SerializeToElement(
            request,
            RuntimeJsonContext.Default.InitializeRequest);
        var response = await _controlChannel!.SendRequestAsync(
            "initialize",
            parameters,
            cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        var result = response.Result
            ?? throw new InvalidDataException("The initialize response did not contain a result.");
        var initializeResponse = JsonSerializer.Deserialize(
            result,
            RuntimeJsonContext.Default.InitializeResponse)
            ?? throw new InvalidDataException("The initialize response was empty.");
        if (!string.Equals(
            initializeResponse.RuntimeInstanceId,
            _runtimeInstanceId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("The initialized Runtime instance did not match the handshake.");
        }

        return initializeResponse;
    }

    private void UpdateConfirmedGsn(long lastConfirmedGsn)
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

    private static void ThrowIfError(JsonRpcResponse response)
    {
        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"Runtime RPC failed ({response.Error.Code}): {response.Error.Message}");
        }
    }

    private void ValidatePeerProcess(NamedPipeTransport transport)
    {
        if (_options.ExpectedServerProcessId is not { } expectedProcessId)
        {
            return;
        }

        if (transport.PeerProcessId is not { } peerProcessId)
        {
            throw new PlatformNotSupportedException(
                "The current transport cannot authenticate the server process identifier.");
        }

        if (peerProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                $"The named pipe server PID {peerProcessId} did not match expected PID {expectedProcessId}.");
        }
    }

    private static CancellationTokenSource? CreateConnectTimeoutSource(
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        if (connectTimeout == default)
        {
            return null;
        }

        var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(connectTimeout);
        return timeoutSource;
    }
}
