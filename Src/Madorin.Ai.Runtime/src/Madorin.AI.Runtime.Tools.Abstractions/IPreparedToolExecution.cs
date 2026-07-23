namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Represents an authorized tool call whose external resources have been resolved and pinned.</summary>
public interface IPreparedToolExecution : IAsyncDisposable
{
    /// <summary>Gets a redacted, normalized target suitable for security audit records.</summary>
    public string TargetSummary { get; }

    /// <summary>Executes the prepared call without resolving a different external target.</summary>
    public Task<ToolResult> ExecuteAsync(CancellationToken ct = default);
}

/// <summary>Adapts executors that do not need to acquire resources during preparation.</summary>
internal sealed class DeferredToolExecution(
    IToolExecutor executor,
    ToolInvocation invocation) : IPreparedToolExecution
{
    private readonly IToolExecutor _executor = executor;
    private readonly ToolInvocation _invocation = invocation;

    public string TargetSummary => $"tool:{_invocation.ToolId}";

    public Task<ToolResult> ExecuteAsync(CancellationToken ct = default) =>
        _executor.ExecuteAsync(_invocation, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
