using System.Collections.Concurrent;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>Full-duplex JSON-RPC and Blob multiplexer over one authenticated connection.</summary>
public sealed class FramedControlChannel : IDuplexRpcPeer
{
    private readonly NamedPipeTransport? _transport;
    private readonly AuthenticatedFrameChannel? _authenticatedChannel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _blobGate = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<long, Task> _requestTasks = new();
    private readonly Task _receivePump;
    private Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>? _requestHandler;
    private Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>?
        _blobRequestHandler;
    private TaskCompletionSource<ReadOnlyMemory<byte>>? _pendingBlobResponse;
    private long _nextId;
    private long _nextRequestTaskId;
    private int _disposed;

    public FramedControlChannel(NamedPipeTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _receivePump = ReceivePumpAsync(_lifetime.Token);
    }

    public FramedControlChannel(AuthenticatedFrameChannel authenticatedChannel)
    {
        _authenticatedChannel = authenticatedChannel
            ?? throw new ArgumentNullException(nameof(authenticatedChannel));
        _receivePump = ReceivePumpAsync(_lifetime.Token);
    }

    public DateTimeOffset? LastSentAt { get; private set; }

    public DateTimeOffset? LastReceivedAt { get; private set; }

    public Task Completion => _receivePump;

    public void SetRequestHandler(
        Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Volatile.Write(ref _requestHandler, handler);
    }

    /// <summary>Registers the server-side Blob request handler for this multiplexed connection.</summary>
    public void SetBlobRequestHandler(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Volatile.Write(ref _blobRequestHandler, handler);
    }

    public ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken ct = default) =>
        SendRequestCoreAsync(method, parameters, timeout: null, ct);

    public ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return SendRequestCoreAsync(method, parameters, timeout, ct);
    }

    public async ValueTask<ReadOnlyMemory<byte>> SendAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        var expectedId = GetMessageId(request);
        var pending = RegisterPending(expectedId);
        try
        {
            await SendFrameAsync(request, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(expectedId, out _);
        }
    }

    /// <summary>Exchanges one Blob frame through the same receive pump.</summary>
    public async ValueTask<ReadOnlyMemory<byte>> ExchangeBlobAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!BlobFrameProtocol.IsBlobFrame(request))
        {
            throw new ArgumentException("The payload is not a Blob frame.", nameof(request));
        }

        await _blobGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = new TaskCompletionSource<ReadOnlyMemory<byte>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pendingBlobResponse, pending);
            await SendFrameAsync(request, ct).ConfigureAwait(false);
            return await pending.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _pendingBlobResponse, null);
            _blobGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        var disposedError = new ObjectDisposedException(nameof(FramedControlChannel));
        CompletePending(disposedError);
        Volatile.Read(ref _pendingBlobResponse)?.TrySetException(disposedError);

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        await ObserveCompletionAsync(_receivePump).ConfigureAwait(false);
        var requestTasks = _requestTasks.Values.ToArray();
        if (requestTasks.Length > 0)
        {
            await Task.WhenAll(requestTasks.Select(ObserveRequestCompletionAsync))
                .ConfigureAwait(false);
        }

        _blobGate.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async ValueTask<JsonRpcResponse> SendRequestCoreAsync(
        string method,
        JsonElement? parameters,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonRpcRequest("2.0", id, method, parameters);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeJsonContext.Default.JsonRpcRequest);

        var timeoutValue = timeout.GetValueOrDefault();
        using var timeoutSource = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutSource?.CancelAfter(timeoutValue);
        var effectiveToken = timeoutSource?.Token ?? ct;
        try
        {
            var responseBytes = await SendAsync(bytes, effectiveToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize(
                responseBytes.Span,
                RuntimeJsonContext.Default.JsonRpcResponse)
                ?? throw new InvalidDataException("The control response was empty.");
            if (!string.Equals(response.JsonRpc, "2.0", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The control response is not JSON-RPC 2.0.");
            }

            return response;
        }
        catch (OperationCanceledException) when (timeoutSource is not null && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"JSON-RPC request '{method}' exceeded the {timeoutValue.TotalMilliseconds:0}-ms timeout.");
        }
    }

    private PendingRequest RegisterPending(long id)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var pending = new PendingRequest();
        if (!_pendingRequests.TryAdd(id, pending))
        {
            throw new InvalidOperationException($"JSON-RPC request id '{id}' is already pending.");
        }

        return pending;
    }

    private async Task ReceivePumpAsync(CancellationToken ct)
    {
        Exception? terminalError = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync(ct).ConfigureAwait(false);
                if (frame.Length == 0)
                {
                    terminalError = new EndOfStreamException("The control channel closed.");
                    break;
                }

                LastReceivedAt = DateTimeOffset.UtcNow;
                if (BlobFrameProtocol.IsBlobFrame(frame))
                {
                    DispatchBlobFrame(frame, ct);
                    continue;
                }

                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (root.TryGetProperty("method", out _))
                {
                    var request = JsonSerializer.Deserialize(
                        frame.Span,
                        RuntimeJsonContext.Default.JsonRpcRequest)
                        ?? throw new InvalidDataException("The JSON-RPC request was empty.");
                    DispatchRequest(request, ct);
                    continue;
                }

                var id = GetMessageId(frame);
                if (_pendingRequests.TryRemove(id, out var pending))
                {
                    pending.Completion.TrySetResult(frame.ToArray());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or JsonException
            or ObjectDisposedException)
        {
            terminalError = ex;
        }
        finally
        {
            CompletePending(terminalError ?? new OperationCanceledException(ct));
            Volatile.Read(ref _pendingBlobResponse)?.TrySetException(
                terminalError ?? new OperationCanceledException(ct));
        }
    }

    private void DispatchRequest(JsonRpcRequest request, CancellationToken ct)
    {
        var taskId = Interlocked.Increment(ref _nextRequestTaskId);
        var task = Task.Run(() => HandleRequestAsync(request, ct), CancellationToken.None);
        _requestTasks.TryAdd(taskId, task);
        _ = RemoveRequestTaskAsync(taskId, task);
    }

    private void DispatchBlobFrame(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var blob = BlobFrameProtocol.Decode(frame);
        if (blob.IsResponse)
        {
            Volatile.Read(ref _pendingBlobResponse)?.TrySetResult(frame.ToArray());
            return;
        }

        var taskId = Interlocked.Increment(ref _nextRequestTaskId);
        var task = Task.Run(() => HandleBlobRequestAsync(frame, ct), CancellationToken.None);
        _requestTasks.TryAdd(taskId, task);
        _ = RemoveRequestTaskAsync(taskId, task);
    }

    private async Task HandleRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        JsonRpcResponse response;
        var handler = Volatile.Read(ref _requestHandler);
        if (handler is null)
        {
            response = new JsonRpcResponse(
                "2.0",
                request.Id,
                Error: new JsonRpcError(-32601, $"Method '{request.Method}' is not registered."));
        }
        else
        {
            try
            {
                response = await handler(request, ct).ConfigureAwait(false);
                if (response.Id != request.Id)
                {
                    throw new InvalidOperationException("The request handler returned a mismatched JSON-RPC id.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or JsonException)
            {
                response = new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32603, ex.Message));
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            response,
            RuntimeJsonContext.Default.JsonRpcResponse);
        await SendFrameAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task HandleBlobRequestAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var handler = Volatile.Read(ref _blobRequestHandler)
            ?? throw new InvalidOperationException("No Blob request handler is registered.");
        var response = await handler(frame, ct).ConfigureAwait(false);
        if (!BlobFrameProtocol.IsBlobFrame(response)
            || !BlobFrameProtocol.Decode(response).IsResponse)
        {
            throw new InvalidDataException("The Blob handler returned an invalid response frame.");
        }

        await SendFrameAsync(response, ct).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LastSentAt = DateTimeOffset.UtcNow;
            if (_authenticatedChannel is not null)
            {
                await _authenticatedChannel.SendFrameAsync(frame, ct).ConfigureAwait(false);
            }
            else
            {
                await _transport!.SendFrameAsync(frame, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ValueTask<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken ct) =>
        _authenticatedChannel is not null
            ? _authenticatedChannel.ReceiveFrameAsync(ct)
            : ReceiveTransportFrameAsync(ct);

    private async ValueTask<ReadOnlyMemory<byte>> ReceiveTransportFrameAsync(CancellationToken ct) =>
        await _transport!.ReceiveFrameAsync(ct).ConfigureAwait(false);

    private void CompletePending(Exception error)
    {
        foreach (var (id, pending) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(id, out _))
            {
                pending.Completion.TrySetException(error);
            }
        }
    }

    private async Task RemoveRequestTaskAsync(long taskId, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or InvalidOperationException
            or ObjectDisposedException)
        {
        }
        finally
        {
            _requestTasks.TryRemove(taskId, out _);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveRequestCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or InvalidOperationException
            or ObjectDisposedException)
        {
        }
    }

    private static long GetMessageId(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        if (!document.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value))
        {
            throw new InvalidDataException("The JSON-RPC message id is missing or invalid.");
        }

        return value;
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<ReadOnlyMemory<byte>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
