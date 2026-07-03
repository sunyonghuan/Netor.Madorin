using Microsoft.Agents.AI;

using Netor.Cortana.AI.Providers;

namespace Netor.Cortana.AI;

/// <summary>
/// turn 内聊天消息持久化薄包装。
/// 阶段 3.1 仅承接“取消时保存部分回复”职责，不替代 AF 钩子主持久化路径。
/// </summary>
public sealed class ChatMessagePersistence(ChatHistoryDataProvider chatHistoryProvider)
{
    public Task SavePartialResponseAsync(
        string partialResponse,
        AgentSession? session,
        AIAgent? agent,
        string modelName,
        string? messageId,
        CancellationToken cancellationToken = default)
    {
        return chatHistoryProvider.SavePartialResponseAsync(
            partialResponse,
            session,
            agent,
            modelName,
            messageId,
            cancellationToken);
    }
}
