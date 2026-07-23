using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Queries durable executor state for calls that were already sent.</summary>
public interface IRecoverableToolExecutor : IToolExecutor
{
    public Task<ToolRecoveryResult> QueryResultAsync(
        ToolInvocation invocation,
        CancellationToken ct = default);
}

public sealed record ToolRecoveryResult(
    ToolCallStatus Status,
    ToolResult? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
