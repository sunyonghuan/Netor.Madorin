using Microsoft.Agents.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 对当前已合并的 AIContext 工具列表执行最终过滤。
/// </summary>
internal sealed class ToolFilteringContextProvider(ToolFilterMode mode) : AIContextProvider
{
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (mode == ToolFilterMode.Full)
        {
            return ValueTask.FromResult(context.AIContext);
        }

        context.AIContext.Tools = ToolFilter.Apply(context.AIContext.Tools, mode);
        return ValueTask.FromResult(context.AIContext);
    }
}
