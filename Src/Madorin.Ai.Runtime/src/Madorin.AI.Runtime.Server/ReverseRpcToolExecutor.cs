using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.Server;

internal sealed class ReverseRpcToolExecutor(
    IDuplexRpcPeer peer,
    IToolResultBlobStore? blobStore = null) : IRecoverableToolExecutor
{
    private static readonly TimeSpan CancellationNotificationTimeout = TimeSpan.FromSeconds(2);
    private readonly IDuplexRpcPeer _peer = peer ?? throw new ArgumentNullException(nameof(peer));
    private readonly IToolResultBlobStore? _blobStore = blobStore;

    public bool CanExecute(string toolId) =>
        toolId.StartsWith("host.", StringComparison.Ordinal)
        || toolId.StartsWith("mcp.", StringComparison.Ordinal);

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ValidateInvocation(invocation);
        var correlationId = Guid.NewGuid().ToString("N");
        using var arguments = JsonDocument.Parse(invocation.ArgumentsJson);
        var request = new ToolCallRequest(
            correlationId,
            invocation.CallId,
            invocation.RunId,
            invocation.SessionId,
            invocation.InvocationId ?? invocation.CallId,
            invocation.AgentId,
            invocation.ToolId,
            invocation.ToolCatalogVersion
                ?? throw new InvalidOperationException("A fixed tool catalog version is required."),
            arguments.RootElement.Clone(),
            invocation.TimeoutMilliseconds
                ?? throw new InvalidOperationException("A tool timeout is required."),
            invocation.PermissionContext?.GrantId
                ?? throw new InvalidOperationException("A tool Grant is required."));
        var parameters = JsonSerializer.SerializeToElement(
            request,
            RuntimeJsonContext.Default.ToolCallRequest);

        JsonRpcResponse response;
        try
        {
            response = await _peer.SendRequestAsync(
                MessageTypes.ToolCallRequest,
                parameters,
                TimeSpan.FromMilliseconds(request.TimeoutMilliseconds),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelAsync(correlationId, invocation.CallId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or EndOfStreamException
            or TimeoutException
            or ObjectDisposedException)
        {
            throw new ToolResultUnavailableException(
                $"The host result for tool call '{invocation.CallId}' is not currently available.",
                ex);
        }

        var result = ReadResponse<ToolCallResponse>(
            response,
            RuntimeJsonContext.Default.ToolCallResponse);
        ValidateResponseIdentity(result.CorrelationId, correlationId, result.CallId, invocation.CallId);
        return await ToToolResultAsync(
            invocation,
            result.Status,
            result.Result,
            result.ResultHash,
            result.ResultBlob,
            result.ErrorMessage,
            ct).ConfigureAwait(false);
    }

    public async Task<ToolRecoveryResult> QueryResultAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ValidateInvocation(invocation);
        var correlationId = Guid.NewGuid().ToString("N");
        var request = new ToolResultQueryRequest(correlationId, invocation.CallId);
        var parameters = JsonSerializer.SerializeToElement(
            request,
            RuntimeJsonContext.Default.ToolResultQueryRequest);
        JsonRpcResponse response;
        try
        {
            response = await _peer.SendRequestAsync(
                MessageTypes.ToolResultQuery,
                parameters,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or EndOfStreamException
            or TimeoutException
            or ObjectDisposedException)
        {
            throw new ToolResultUnavailableException(
                $"The host cannot currently query tool call '{invocation.CallId}'.",
                ex);
        }

        var result = ReadResponse<ToolResultQueryResponse>(
            response,
            RuntimeJsonContext.Default.ToolResultQueryResponse);
        ValidateResponseIdentity(result.CorrelationId, correlationId, result.CallId, invocation.CallId);
        var toolResult = result.Status is ToolCallStatus.Succeeded
            or ToolCallStatus.Failed
            or ToolCallStatus.Cancelled
                ? await ToToolResultAsync(
                    invocation,
                    result.Status,
                    result.Result,
                    result.ResultHash,
                    result.ResultBlob,
                    result.ErrorMessage,
                    ct).ConfigureAwait(false)
                : null;
        return new ToolRecoveryResult(
            result.Status,
            toolResult,
            result.ErrorCode,
            result.ErrorMessage);
    }

    public Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionRequest request,
        CancellationToken ct = default) =>
        SendTypedRequestAsync(
            MessageTypes.ToolPermissionRequest,
            request,
            RuntimeJsonContext.Default.ToolPermissionRequest,
            RuntimeJsonContext.Default.ToolPermissionResponse,
            ct);

    public Task<ApprovalResponse> RequestApprovalAsync(
        ApprovalRequest request,
        CancellationToken ct = default) =>
        SendTypedRequestAsync(
            MessageTypes.ApprovalRequest,
            request,
            RuntimeJsonContext.Default.ApprovalRequest,
            RuntimeJsonContext.Default.ApprovalResponse,
            ct);

    private async Task<TResponse> SendTypedRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken ct)
    {
        var parameters = JsonSerializer.SerializeToElement(request, requestType);
        var response = await _peer.SendRequestAsync(
            method,
            parameters,
            TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);
        return ReadResponse(response, responseType);
    }

    private async Task TryCancelAsync(string correlationId, string callId)
    {
        try
        {
            var request = new ToolCallCancelRequest(correlationId, callId, "runtime-cancelled");
            var parameters = JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolCallCancelRequest);
            _ = await _peer.SendRequestAsync(
                MessageTypes.ToolCallCancel,
                parameters,
                CancellationNotificationTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or EndOfStreamException
            or TimeoutException
            or ObjectDisposedException)
        {
        }
    }

    private static T ReadResponse<T>(
        JsonRpcResponse response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (response.Error is { } error)
        {
            throw new InvalidOperationException(
                $"Reverse RPC failed with {error.Code}: {error.Message}");
        }

        if (response.Result is not { } result)
        {
            throw new InvalidDataException("Reverse RPC returned no result.");
        }

        return JsonSerializer.Deserialize(result, typeInfo)
            ?? throw new InvalidDataException("Reverse RPC returned an empty result.");
    }

    private async Task<ToolResult> ToToolResultAsync(
        ToolInvocation invocation,
        ToolCallStatus status,
        JsonElement? result,
        string? resultHash,
        BlobReference? resultBlob,
        string? errorMessage,
        CancellationToken ct)
    {
        if (status is not ToolCallStatus.Succeeded
            and not ToolCallStatus.Failed
            and not ToolCallStatus.Cancelled)
        {
            throw new ToolResultUnavailableException(
                $"Host tool call '{invocation.CallId}' is in state '{status}'.");
        }

        var success = status is ToolCallStatus.Succeeded;
        var outputJson = result?.GetRawText();
        if (success && outputJson is null && resultBlob is null)
        {
            throw new InvalidDataException(
                $"Host tool call '{invocation.CallId}' succeeded without result content.");
        }

        if (resultBlob is not null)
        {
            if (_blobStore is null)
            {
                throw new InvalidDataException(
                    "The Runtime cannot accept a Blob result without a configured Blob store.");
            }

            await _blobStore.ValidateReferenceAsync(resultBlob, ct).ConfigureAwait(false);
        }

        var actualResultHash = outputJson is not null
            ? ComputeHash(outputJson)
            : resultBlob?.Sha256;
        if (resultHash is not null
            && !string.Equals(resultHash, actualResultHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The host tool result hash does not match its content.");
        }

        return new ToolResult(
            invocation.CallId,
            invocation.ToolId,
            success,
            outputJson ?? (resultBlob is null ? "{}" : null),
            success ? null : errorMessage ?? $"Host tool call ended with status '{status}'.",
            resultBlob,
            actualResultHash);
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void ValidateInvocation(ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanExecute(invocation.ToolId))
        {
            throw new ArgumentException(
                $"Tool '{invocation.ToolId}' is not host-owned.",
                nameof(invocation));
        }
    }

    private static void ValidateResponseIdentity(
        string actualCorrelationId,
        string expectedCorrelationId,
        string actualCallId,
        string expectedCallId)
    {
        if (!string.Equals(actualCorrelationId, expectedCorrelationId, StringComparison.Ordinal)
            || !string.Equals(actualCallId, expectedCallId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Reverse RPC returned a response for another tool call.");
        }
    }
}
