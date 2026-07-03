using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 将子智能体的 <see cref="AIFunction"/> 作为工具注入到主智能体的上下文中。
/// </summary>
internal sealed class SubAgentContextProvider(IReadOnlyList<AIFunction> agentFunctions) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AIContext>(new AIContext
        {
            Tools = agentFunctions
        });
    }
}
