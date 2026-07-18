using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI;

/// <summary>
/// 负责专家模式聊天消息的落库写入。
/// </summary>
public sealed class ChatMessageWriter(
    CortanaDbContext dbContext,
    ILogger<ChatMessageWriter> logger)
{
    public void SaveUserMessage(
        AIChatMessage message,
        AgentSession session,
        AgentEntity agentEntity,
        AiModelEntity model)
    {
        try
        {
            var sessionId = session.StateBag.GetValue<string>("sessionid") ?? string.Empty;
            var userAgentId = session.StateBag.GetValue<string>("agentid") ?? agentEntity.Id ?? string.Empty;
            var userAgentName = session.StateBag.GetValue<string>("agentname") ?? agentEntity.Name ?? string.Empty;
            var entity = new ChatMessageEntity
            {
                Role = ChatRole.User.ToString(),
                Content = message.ToPersistedContent(),
                ContentsJson = ChatMessageExtensions.BuildContentsJson(message.Contents),
                AuthorName = "用户",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id = message.MessageId ?? Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = model.Name,
                AgentId = userAgentId,
                AgentName = userAgentName
            };

            dbContext.Execute(
                """
                INSERT OR REPLACE INTO ChatMessages
                    (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName)
                VALUES
                    (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt, @AgentId, @AgentName)
                """,
                cmd => ChatMessageService.BindEntity(cmd, entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存用户消息失败");
        }
    }

    public void SaveAssistantMessage(
        string messageId,
        string sessionId,
        string content,
        AgentSession session,
        AgentEntity agentEntity,
        string modelName)
    {
        try
        {
            var assistantAgentId = session.StateBag.GetValue<string>("agentid") ?? agentEntity.Id ?? string.Empty;
            var assistantAgentName = session.StateBag.GetValue<string>("agentname") ?? agentEntity.Name ?? string.Empty;
            var contents = new List<AIContent> { new TextContent(content) };
            var entity = new ChatMessageEntity
            {
                Role = ChatRole.Assistant.ToString(),
                Content = content,
                ContentsJson = ChatMessageExtensions.BuildContentsJson(contents),
                AuthorName = string.IsNullOrWhiteSpace(assistantAgentName) ? "AI" : assistantAgentName,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id = messageId,
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = modelName,
                AgentId = assistantAgentId,
                AgentName = assistantAgentName
            };

            dbContext.Execute(
                """
                INSERT OR REPLACE INTO ChatMessages
                    (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName)
                VALUES
                    (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt, @AgentId, @AgentName)
                """,
                cmd => ChatMessageService.BindEntity(cmd, entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存助手消息失败");
        }
    }
}
