using Netor.Cortana.AI.Orchestration;

namespace Netor.Cortana.AI;

/// <summary>
/// 对话事件统一元数据。
/// 阶段 3.2 抽出后由 <see cref="AiChatHostedService"/> 负责组装，
/// 再交给事件发布器负责构造具体 EventArgs。
/// </summary>
public sealed record ConversationEventMetadata(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    AgentOrchestrationMode OrchestrationMode = AgentOrchestrationMode.None,
    IReadOnlyList<string>? OrchestrationAgentIds = null,
    IReadOnlyList<string>? OrchestrationWarnings = null);
