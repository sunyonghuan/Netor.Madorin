using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI;

/// <summary>
/// 发布聊天会话相关 EventHub 事件。
/// 阶段 3.2 从 <see cref="AiChatHostedService"/> 中抽出事件构造职责，
/// 保持事件结构与触发时机不变。
/// </summary>
public sealed class ChatConversationEventPublisher(
    IPublisher publisher,
    ChatOrchestrationDiagnosticsService? diagnosticsService = null,
    ILogger<ChatConversationEventPublisher>? logger = null)
{
    private static readonly Action<ILogger, string, string, string, int, Exception?> LogTurnStarted =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Debug,
            new EventId(1, nameof(PublishTurnStarted)),
            "发布 ConversationTurnStarted。Turn={TurnId} Mode={Mode} Agents={Agents} WarningCount={WarningCount}");

    private static readonly Action<ILogger, string, string, string, int, Exception?> LogTurnCompleted =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Debug,
            new EventId(2, nameof(PublishTurnCompleted)),
            "发布 ConversationTurnCompleted。Turn={TurnId} Mode={Mode} Agents={Agents} WarningCount={WarningCount}");

    private readonly ILogger<ChatConversationEventPublisher> _logger = logger ?? NullLogger<ChatConversationEventPublisher>.Instance;
    private readonly ChatOrchestrationDiagnosticsService? _diagnosticsService = diagnosticsService;

    public void PublishTurnStarted(
        ConversationEventMetadata? metadata,
        int attachmentCount,
        IReadOnlyList<string> mentionedAgentIds)
    {
        if (metadata is null)
        {
            return;
        }

        _diagnosticsService?.RecordTurnStarted(metadata);
        LogMetadata(metadata, LogTurnStarted);
        publisher.Publish(Events.OnConversationTurnStarted, new ConversationTurnStartedArgs(
            metadata.SessionId,
            metadata.TurnId,
            metadata.TraceId,
            metadata.ProviderId,
            metadata.ProviderName,
            metadata.AgentId,
            metadata.AgentName,
            metadata.ModelId,
            metadata.ModelName,
            metadata.UserMessageId,
            metadata.AssistantMessageId,
            DateTimeOffset.UtcNow,
            attachmentCount,
            mentionedAgentIds));
    }

    public void PublishUserMessage(
        ConversationEventMetadata? metadata,
        string content,
        IReadOnlyList<AttachmentInfo> attachments)
    {
        if (metadata is null)
        {
            return;
        }

        publisher.Publish(Events.OnConversationUserMessage, new ConversationUserMessageArgs(
            metadata.SessionId,
            metadata.TurnId,
            metadata.TraceId,
            metadata.ProviderId,
            metadata.ProviderName,
            metadata.AgentId,
            metadata.AgentName,
            metadata.ModelId,
            metadata.ModelName,
            metadata.UserMessageId,
            metadata.AssistantMessageId,
            DateTimeOffset.UtcNow,
            content,
            attachments));
    }

    public void PublishAssistantDelta(
        ConversationEventMetadata? metadata,
        string delta,
        int sequence)
    {
        if (metadata is null)
        {
            return;
        }

        publisher.Publish(Events.OnConversationAssistantDelta, new ConversationAssistantDeltaArgs(
            metadata.SessionId,
            metadata.TurnId,
            metadata.TraceId,
            metadata.ProviderId,
            metadata.ProviderName,
            metadata.AgentId,
            metadata.AgentName,
            metadata.ModelId,
            metadata.ModelName,
            metadata.UserMessageId,
            metadata.AssistantMessageId,
            DateTimeOffset.UtcNow,
            delta,
            sequence));
    }

    public void PublishTurnCompleted(
        ConversationEventMetadata? metadata,
        ConversationTurnStatus status,
        string userInput,
        string assistantResponse,
        string? errorMessage,
        int assistantDeltaCount,
        int attachmentCount)
    {
        if (metadata is null)
        {
            return;
        }

        _diagnosticsService?.RecordTurnCompleted(metadata, status);
        LogMetadata(metadata, LogTurnCompleted);
        publisher.Publish(Events.OnConversationTurnCompleted, new ConversationTurnCompletedArgs(
            metadata.SessionId,
            metadata.TurnId,
            metadata.TraceId,
            metadata.ProviderId,
            metadata.ProviderName,
            metadata.AgentId,
            metadata.AgentName,
            metadata.ModelId,
            metadata.ModelName,
            metadata.UserMessageId,
            metadata.AssistantMessageId,
            DateTimeOffset.UtcNow,
            status,
            userInput,
            assistantResponse,
            errorMessage,
            assistantDeltaCount,
            attachmentCount));
    }

    private void LogMetadata(
        ConversationEventMetadata metadata,
        Action<ILogger, string, string, string, int, Exception?> log)
    {
        var agents = metadata.OrchestrationAgentIds is { Count: > 0 }
            ? string.Join(",", metadata.OrchestrationAgentIds)
            : "none";
        var warnings = metadata.OrchestrationWarnings?.Count ?? 0;
        log(_logger, metadata.TurnId, metadata.OrchestrationMode.ToString(), agents, warnings, null);
    }
}
