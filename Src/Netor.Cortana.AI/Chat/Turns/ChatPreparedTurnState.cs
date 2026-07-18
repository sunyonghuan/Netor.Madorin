using Microsoft.Agents.AI;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 承载已完成 Agent/Session 准备的 turn 执行状态。
/// </summary>
public sealed record ChatPreparedTurnState(
    AIAgent Agent,
    AgentSession Session,
    AiProviderEntity Provider,
    AgentEntity AgentEntity,
    AiModelEntity Model,
    AgentOrchestrationResult? OrchestrationResult = null);
