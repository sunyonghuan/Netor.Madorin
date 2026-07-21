namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolExecutor
{
    public ValueTask<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
