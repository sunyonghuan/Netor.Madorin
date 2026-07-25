using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Client;

internal sealed class RuntimeHostCallbackDispatcher : IDisposable
{
    private const int CallbackTimeoutError = -32020;
    private const int CallbackCancelledError = -32021;
    private const int CallbackFailedError = -32022;

    private readonly RuntimeHostCallbacks _callbacks;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    public RuntimeHostCallbackDispatcher(RuntimeHostCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(callbacks.MaxConcurrency, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            callbacks.CallbackTimeout,
            TimeSpan.Zero);

        _callbacks = callbacks;
        _concurrency = new SemaphoreSlim(callbacks.MaxConcurrency, callbacks.MaxConcurrency);
    }

    public async ValueTask<JsonRpcResponse> HandleAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!HasCallback(request.Method))
        {
            return Error(
                request,
                -32601,
                $"Host callback method '{request.Method}' is not registered.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(_callbacks.CallbackTimeout);
        var entered = false;
        try
        {
            await _concurrency.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            entered = true;
            return await DispatchAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(
                request,
                CallbackTimeoutError,
                $"Host callback '{request.Method}' exceeded its timeout.");
        }
        catch (OperationCanceledException)
        {
            return Error(
                request,
                CallbackCancelledError,
                $"Host callback '{request.Method}' was cancelled.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Error(request, -32602, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(
                request,
                CallbackFailedError,
                $"Host callback '{request.Method}' failed: {ex.Message}");
        }
        finally
        {
            if (entered)
            {
                _concurrency.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _concurrency.Dispose();
        }
    }

    private bool HasCallback(string method) => method switch
    {
        MessageTypes.ToolPermissionRequest => _callbacks.ToolPermissionRequestedAsync is not null,
        MessageTypes.ApprovalRequest => _callbacks.ApprovalRequestedAsync is not null,
        MessageTypes.ToolCallRequest => _callbacks.ToolCallRequestedAsync is not null,
        MessageTypes.ToolResultQuery => _callbacks.ToolResultQueriedAsync is not null,
        MessageTypes.ToolCallCancel => _callbacks.ToolCallCancelledAsync is not null,
        _ => false
    };

    private ValueTask<JsonRpcResponse> DispatchAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken) => request.Method switch
        {
            MessageTypes.ToolPermissionRequest => DispatchPermissionAsync(
                request,
                cancellationToken),
            MessageTypes.ApprovalRequest => DispatchApprovalAsync(request, cancellationToken),
            MessageTypes.ToolCallRequest => DispatchToolCallAsync(request, cancellationToken),
            MessageTypes.ToolResultQuery => DispatchToolResultQueryAsync(
                request,
                cancellationToken),
            MessageTypes.ToolCallCancel => DispatchToolCallCancelAsync(
                request,
                cancellationToken),
            _ => ValueTask.FromResult(Error(request, -32601, "Host callback is not registered."))
        };

    private async ValueTask<JsonRpcResponse> DispatchPermissionAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize(
            request,
            RuntimeJsonContext.Default.ToolPermissionRequest);
        var response = await _callbacks.ToolPermissionRequestedAsync!(
            parameters,
            cancellationToken).ConfigureAwait(false);
        EnsureResponse(response, request.Method);
        EnsureEqual(
            parameters.CorrelationId ?? parameters.ApprovalRequestId,
            response.CorrelationId,
            "correlationId");
        EnsureEqual(parameters.CallId, response.CallId, "callId");
        return Success(request, response, RuntimeJsonContext.Default.ToolPermissionResponse);
    }

    private async ValueTask<JsonRpcResponse> DispatchApprovalAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize(request, RuntimeJsonContext.Default.ApprovalRequest);
        var response = await _callbacks.ApprovalRequestedAsync!(
            parameters,
            cancellationToken).ConfigureAwait(false);
        EnsureResponse(response, request.Method);
        EnsureEqual(parameters.CorrelationId, response.CorrelationId, "correlationId");
        EnsureEqual(
            parameters.ApprovalRequestId,
            response.ApprovalRequestId,
            "approvalRequestId");
        return Success(request, response, RuntimeJsonContext.Default.ApprovalResponse);
    }

    private async ValueTask<JsonRpcResponse> DispatchToolCallAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize(request, RuntimeJsonContext.Default.ToolCallRequest);
        var response = await _callbacks.ToolCallRequestedAsync!(
            parameters,
            cancellationToken).ConfigureAwait(false);
        EnsureResponse(response, request.Method);
        EnsureEqual(parameters.CorrelationId, response.CorrelationId, "correlationId");
        EnsureEqual(parameters.CallId, response.CallId, "callId");
        return Success(request, response, RuntimeJsonContext.Default.ToolCallResponse);
    }

    private async ValueTask<JsonRpcResponse> DispatchToolResultQueryAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize(
            request,
            RuntimeJsonContext.Default.ToolResultQueryRequest);
        var response = await _callbacks.ToolResultQueriedAsync!(
            parameters,
            cancellationToken).ConfigureAwait(false);
        EnsureResponse(response, request.Method);
        EnsureEqual(parameters.CorrelationId, response.CorrelationId, "correlationId");
        EnsureEqual(parameters.CallId, response.CallId, "callId");
        return Success(request, response, RuntimeJsonContext.Default.ToolResultQueryResponse);
    }

    private async ValueTask<JsonRpcResponse> DispatchToolCallCancelAsync(
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = Deserialize(
            request,
            RuntimeJsonContext.Default.ToolCallCancelRequest);
        var response = await _callbacks.ToolCallCancelledAsync!(
            parameters,
            cancellationToken).ConfigureAwait(false);
        return Success(request, response, RuntimeJsonContext.Default.Boolean);
    }

    private static T Deserialize<T>(JsonRpcRequest request, JsonTypeInfo<T> typeInfo) =>
        request.Params is { } parameters
            ? JsonSerializer.Deserialize(parameters, typeInfo)
                ?? throw new InvalidDataException(
                    $"Host callback '{request.Method}' had an empty payload.")
            : throw new InvalidDataException(
                $"Host callback '{request.Method}' did not contain parameters.");

    private static JsonRpcResponse Success<T>(
        JsonRpcRequest request,
        T response,
        JsonTypeInfo<T> typeInfo) => new(
            "2.0",
            request.Id,
            JsonSerializer.SerializeToElement(response, typeInfo));

    private static JsonRpcResponse Error(JsonRpcRequest request, int code, string message) =>
        new("2.0", request.Id, Error: new JsonRpcError(code, message));

    private static void EnsureResponse<T>(T? response, string method)
        where T : class
    {
        if (response is null)
        {
            throw new InvalidOperationException(
                $"Host callback '{method}' returned no response.");
        }
    }

    private static void EnsureEqual(string expected, string actual, string fieldName)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Host callback response {fieldName} does not match the request.");
        }
    }
}
