using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 默认 <see cref="IChatCompactionClientResolver"/> 实现。
/// 把 <see cref="ChatHistoryDataProvider.ResolveCompactionClient"/> 的逻辑搬到公共组件，
/// Chat 路径与 Workflow 路径（WorkflowTitleGenerator）共享同一解析链路。
/// </summary>
public sealed class ChatCompactionClientResolver(IServiceProvider services) : IChatCompactionClientResolver
{
    public IChatClient? Resolve(AIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // 1. 系统设置 Compaction.ModelId 优先：复用 ModelPurposeResolver 缓存的客户端。
        var resolver = services.GetRequiredService<ModelPurposeResolver>();
        var purposeClient = resolver.TryResolve("Compaction.ModelId");
        if (purposeClient is not null) return purposeClient;

        // 2. 回退到当前 Agent 自身的 IChatClient。
        return agent.GetService<IChatClient>();
    }
}
