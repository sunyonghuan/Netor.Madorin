using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 生成型 turn 的公共运行态快照。
/// </summary>
public sealed record ChatGenerationTurnState(
    string TurnId,
    string GenerationKind,
    string QueuedMessage,
    string TraceId,
    string UserMessageId,
    string AssistantMessageId,
    string CurrentSessionId,
    ConversationEventMetadata Metadata,
    ChatTurnContext TurnContext,
    CancellationTokenSource StreamCts)
{
    public string GenerationTitle => $"{GenerationKind}生成";

    public string ProcessId => $"{GenerationKind}-generation-{TurnId}";
}
