using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 描述一次 Chat 编排构建请求。
/// 阶段 5 先固化契约，后续编排阶段再接入真实执行器。
/// </summary>
public sealed record AgentOrchestrationRequest(
    AgentOrchestrationMode Mode,
    AgentExecutionStrategy ExecutionStrategy,
    AgentEntity MainAgent,
    AiProviderEntity Provider,
    AiModelEntity Model,
    IReadOnlyList<AgentMention> Mentions,
    IReadOnlyList<AttachmentInfo> Attachments,
    string? SessionId,
    string? WorkspaceId,
    string? UserInput);
