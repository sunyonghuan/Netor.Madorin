namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolExecutor
{
    public bool CanExecute(string toolId);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPreparedToolExecution>(
            new DeferredToolExecution(this, invocation));
    }

    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default);
}
