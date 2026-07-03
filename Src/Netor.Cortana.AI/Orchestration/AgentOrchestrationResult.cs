using Microsoft.Agents.AI;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 承载最近一次 Chat 编排构建的结果快照。
/// </summary>
public sealed record AgentOrchestrationResult(
    AIAgent Agent,
    AgentOrchestrationMode Mode,
    IReadOnlyList<string> UsedAgentIds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures);
