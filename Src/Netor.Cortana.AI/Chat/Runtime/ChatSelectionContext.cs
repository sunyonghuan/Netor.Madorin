using Netor.Cortana.AI.Handoff;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 封装当前默认 Provider / Agent / Model 与 handoff 运行时上下文快照。
/// </summary>
public sealed record ChatSelectionContext(
    AiProviderEntity Provider,
    AgentEntity AgentEntity,
    AiModelEntity Model,
    string? SessionId,
    string? WorkspaceId)
{
    public HandoffRuntimeContext CreateRuntimeContext()
    {
        return new HandoffRuntimeContext(
            SessionId,
            WorkspaceId,
            AgentEntity.Id,
            Provider.Id,
            Model.Id);
    }
}
