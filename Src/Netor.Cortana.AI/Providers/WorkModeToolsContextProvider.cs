using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 将工作模式工具注入到智能体的上下文中。
/// 用于总经理 Agent 构建时注入 set_plan / dispatch_step 等工作模式专用工具。
/// </summary>
internal sealed class WorkModeToolsContextProvider(IReadOnlyList<AIFunction> workModeTools) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AIContext>(new AIContext
        {
            Tools = workModeTools
        });
    }
}
