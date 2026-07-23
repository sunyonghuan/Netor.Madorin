using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.Server;

/// <summary>Routes outgoing reverse RPC through the current authenticated host connection.</summary>
internal sealed class ReconnectableDuplexRpcPeer(
    Func<IDuplexRpcPeer?> peerResolver) : IDuplexRpcPeer
{
    private static readonly TimeSpan PeerPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly Func<IDuplexRpcPeer?> _peerResolver = peerResolver
        ?? throw new ArgumentNullException(nameof(peerResolver));

    public Task Completion => Task.CompletedTask;

    public void SetRequestHandler(
        Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler) =>
        throw new NotSupportedException("The reconnectable peer only routes outgoing requests.");

    public ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken ct = default) =>
        SendRequestCoreAsync(method, parameters, ct);

    public async ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await SendRequestCoreAsync(method, parameters, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Reverse RPC method '{method}' did not complete before its timeout.",
                ex);
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> SendAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use typed reverse RPC requests.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<JsonRpcResponse> SendRequestCoreAsync(
        string method,
        JsonElement? parameters,
        CancellationToken ct)
    {
        IDuplexRpcPeer? previousPeer = null;
        while (true)
        {
            var peer = await WaitForPeerAsync(previousPeer, ct).ConfigureAwait(false);
            try
            {
                var response = await peer.SendRequestAsync(method, parameters, ct)
                    .ConfigureAwait(false);
                if (response.Error?.Code == -32601)
                {
                    await Task.Delay(PeerPollInterval, ct).ConfigureAwait(false);
                    previousPeer = null;
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (IsConnectionFailure(ex) && !ct.IsCancellationRequested)
            {
                if (string.Equals(method, MessageTypes.ToolCallRequest, StringComparison.Ordinal))
                {
                    return await RecoverToolCallAsync(parameters, peer, ct).ConfigureAwait(false);
                }

                previousPeer = peer;
            }
        }
    }

    private async Task<JsonRpcResponse> RecoverToolCallAsync(
        JsonElement? parameters,
        IDuplexRpcPeer failedPeer,
        CancellationToken ct)
    {
        if (parameters is not { } requestParameters)
        {
            throw new InvalidDataException("A reverse tool call has no parameters.");
        }

        var call = JsonSerializer.Deserialize(
            requestParameters,
            RuntimeJsonContext.Default.ToolCallRequest)
            ?? throw new InvalidDataException("The reverse tool call is empty.");
        IDuplexRpcPeer? previousPeer = failedPeer;
        while (true)
        {
            var peer = await WaitForPeerAsync(previousPeer, ct).ConfigureAwait(false);
            var queryCorrelationId = Guid.NewGuid().ToString("N");
            var query = new ToolResultQueryRequest(queryCorrelationId, call.CallId);
            var queryParameters = JsonSerializer.SerializeToElement(
                query,
                RuntimeJsonContext.Default.ToolResultQueryRequest);
            JsonRpcResponse response;
            try
            {
                response = await peer.SendRequestAsync(
                    MessageTypes.ToolResultQuery,
                    queryParameters,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionFailure(ex) && !ct.IsCancellationRequested)
            {
                previousPeer = peer;
                continue;
            }

            if (response.Error?.Code == -32601)
            {
                await Task.Delay(PeerPollInterval, ct).ConfigureAwait(false);
                previousPeer = null;
                continue;
            }

            if (response.Error is not null || response.Result is not { } resultElement)
            {
                return response;
            }

            var result = JsonSerializer.Deserialize(
                resultElement,
                RuntimeJsonContext.Default.ToolResultQueryResponse)
                ?? throw new InvalidDataException("The tool result query returned no result.");
            if (!string.Equals(result.CorrelationId, queryCorrelationId, StringComparison.Ordinal)
                || !string.Equals(result.CallId, call.CallId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The reconnected host returned a result for another tool call.");
            }

            if (result.Status is ToolCallStatus.Pending or ToolCallStatus.Running)
            {
                await Task.Delay(PeerPollInterval, ct).ConfigureAwait(false);
                previousPeer = null;
                continue;
            }

            var recovered = new ToolCallResponse(
                call.CorrelationId,
                call.CallId,
                result.Status,
                result.Result,
                result.ResultHash,
                result.ResultBlob,
                result.ErrorCode,
                result.ErrorMessage);
            return new JsonRpcResponse(
                "2.0",
                response.Id,
                JsonSerializer.SerializeToElement(
                    recovered,
                    RuntimeJsonContext.Default.ToolCallResponse));
        }
    }

    private async Task<IDuplexRpcPeer> WaitForPeerAsync(
        IDuplexRpcPeer? previousPeer,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var peer = _peerResolver();
            if (peer is not null && !ReferenceEquals(peer, previousPeer))
            {
                return peer;
            }

            await Task.Delay(PeerPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or TimeoutException or ObjectDisposedException;
}
