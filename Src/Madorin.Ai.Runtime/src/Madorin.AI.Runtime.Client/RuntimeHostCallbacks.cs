using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Client;

/// <summary>Configures typed callbacks for Runtime-initiated host operations.</summary>
public sealed record RuntimeHostCallbacks
{
    /// <summary>Handles a Runtime tool permission request.</summary>
    public Func<ToolPermissionRequest, CancellationToken, ValueTask<ToolPermissionResponse>>?
        ToolPermissionRequestedAsync { get; init; }

    /// <summary>Handles a Runtime user approval request.</summary>
    public Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalResponse>>?
        ApprovalRequestedAsync { get; init; }

    /// <summary>Executes a host or MCP tool requested by the Runtime.</summary>
    public Func<ToolCallRequest, CancellationToken, ValueTask<ToolCallResponse>>?
        ToolCallRequestedAsync { get; init; }

    /// <summary>Queries the durable result of a previously issued tool call.</summary>
    public Func<ToolResultQueryRequest, CancellationToken, ValueTask<ToolResultQueryResponse>>?
        ToolResultQueriedAsync { get; init; }

    /// <summary>Cancels a host tool call.</summary>
    public Func<ToolCallCancelRequest, CancellationToken, ValueTask<bool>>?
        ToolCallCancelledAsync { get; init; }

    /// <summary>Gets the maximum number of callbacks that may execute concurrently.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Gets the timeout applied to queueing and executing each callback.</summary>
    public TimeSpan CallbackTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
