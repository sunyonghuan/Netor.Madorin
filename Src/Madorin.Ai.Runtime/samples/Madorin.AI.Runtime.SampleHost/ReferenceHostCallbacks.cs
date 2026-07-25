using System.Collections.Concurrent;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

internal sealed class ReferenceHostCallbacks
{
    private readonly ConcurrentDictionary<string, ToolCallResponse> _results =
        new(StringComparer.Ordinal);
    private readonly string _workspace;
    private int _approvalCount;
    private int _permissionCount;
    private int _toolCallCount;
    private int _toolQueryCount;

    public ReferenceHostCallbacks(string workspace)
    {
        _workspace = workspace;
    }

    public int ApprovalCount => Volatile.Read(ref _approvalCount);

    public int PermissionCount => Volatile.Read(ref _permissionCount);

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);

    public int ToolQueryCount => Volatile.Read(ref _toolQueryCount);

    public RuntimeHostCallbacks CreateConfiguration() => new()
    {
        MaxConcurrency = 2,
        CallbackTimeout = TimeSpan.FromSeconds(15),
        ToolPermissionRequestedAsync = HandlePermissionAsync,
        ApprovalRequestedAsync = HandleApprovalAsync,
        ToolCallRequestedAsync = HandleToolCallAsync,
        ToolResultQueriedAsync = HandleToolQueryAsync,
        ToolCallCancelledAsync = HandleToolCancellationAsync
    };

    public ValueTask<ToolResultQueryResponse> QueryKnownResultAsync(
        string callId,
        CancellationToken cancellationToken) => HandleToolQueryAsync(
            new ToolResultQueryRequest(Guid.NewGuid().ToString("N"), callId),
            cancellationToken);

    private ValueTask<ToolPermissionResponse> HandlePermissionAsync(
        ToolPermissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _permissionCount);
        var grant = CreateGrant(
            request.RunId ?? throw new InvalidDataException(
                "The permission request did not identify its Run."),
            request.AgentId,
            request.ToolId,
            request.CallId);
        return ValueTask.FromResult(
            new ToolPermissionResponse(
                request.CorrelationId ?? request.ApprovalRequestId,
                request.CallId,
                ToolAuthorizationDecision.Granted,
                grant));
    }

    private ValueTask<ApprovalResponse> HandleApprovalAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _approvalCount);
        var grant = CreateGrant(
            request.RunId,
            request.AgentId,
            request.ToolId,
            request.CallId) with
        {
            ApprovalRequestId = request.ApprovalRequestId
        };
        return ValueTask.FromResult(
            new ApprovalResponse(
                request.CorrelationId,
                request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                grant));
    }

    private ValueTask<ToolCallResponse> HandleToolCallAsync(
        ToolCallRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _toolCallCount);
        var response = new ToolCallResponse(
            request.CorrelationId,
            request.CallId,
            ToolCallStatus.Succeeded,
            ParseElement("""{"value":"reference-host"}"""));
        _results[request.CallId] = response;
        return ValueTask.FromResult(response);
    }

    private ValueTask<ToolResultQueryResponse> HandleToolQueryAsync(
        ToolResultQueryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _toolQueryCount);
        if (!_results.TryGetValue(request.CallId, out var result))
        {
            return ValueTask.FromResult(
                new ToolResultQueryResponse(
                    request.CorrelationId,
                    request.CallId,
                    ToolCallStatus.Unknown));
        }

        return ValueTask.FromResult(
            new ToolResultQueryResponse(
                request.CorrelationId,
                request.CallId,
                result.Status,
                result.Result,
                result.ResultHash,
                result.ResultBlob,
                result.ErrorCode,
                result.ErrorMessage));
    }

    private ValueTask<bool> HandleToolCancellationAsync(
        ToolCallCancelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_results.ContainsKey(request.CallId));
    }

    private ToolGrant CreateGrant(
        string runId,
        string agentId,
        string toolId,
        string callId) => new(
            $"reference-grant-{callId}",
            runId,
            _workspace,
            [_workspace],
            [_workspace],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            DateTimeOffset.UtcNow.AddMinutes(5),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [toolId],
            AllowedCallIds: [callId],
            MaximumRisk: ToolRiskLevel.Low,
            AgentId: agentId,
            RootGrantId: $"reference-grant-{callId}");

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
