using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Security.Cryptography;
using System.Text;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI;

/// <summary>
/// 承接专家模式聊天文本 turn 的执行与取消。
/// 阶段 3.6 仅迁移 <see cref="AiChatHostedService.SendMessageAsync"/> 主路径。
/// </summary>
public sealed class ChatTurnExecutor(
    ChatTurnCancellationRegistry turnRegistry,
    ChatAttachmentLoader attachmentLoader,
    ChatMessagePersistence chatMessagePersistence,
    ChatConversationEventPublisher conversationEventPublisher,
    ChatRealtimeEventPublisher realtimeEventPublisher,
    ChatMessageAssetService assetService,
    IAppPaths appPaths,
    IEnumerable<IAiOutputChannel> outputChannels,
    IPublisher publisher,
    ILogger<ChatTurnExecutor> logger)
{
    public async Task CancelCurrentTurnAsync(
        Func<ChatTurnContext?, CancellationToken, Task> notifyChannelsCancelledAsync,
        CancellationToken cancellationToken = default)
    {
        var turnContext = turnRegistry.CancelCurrentTurn();
        await notifyChannelsCancelledAsync(turnContext, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(
        string userInput,
        IReadOnlyList<AttachmentInfo> attachments,
        IReadOnlyList<AgentMention> mentions,
        AIAgent turnAgent,
        AgentSession turnSession,
        AiProviderEntity turnProvider,
        AgentEntity turnAgentEntity,
        AiModelEntity turnModel,
        AgentOrchestrationResult? orchestrationResult,
        Action<AIChatMessage, AgentSession, AgentEntity, AiModelEntity> saveUserMessage,
        Action<ChatTurnContext?, ConversationTurnStatus, string, string, string?, int, int> publishTurnCompletedOnce,
        Func<string, string, string, string, string, AiProviderEntity, AgentEntity, AiModelEntity, AgentOrchestrationResult?, ConversationEventMetadata> createConversationEventMetadata,
        Func<ChatTurnContext?, CancellationToken, Task> notifyChannelsCancelledAsync,
        Action invalidateCurrentAgent,
        CancellationToken cancellationToken)
    {
        publisher.Publish(Events.OnAiStarted, new VoiceSignalArgs());

        var fullResponse = new StringBuilder();
        var turnId = Guid.NewGuid().ToString("N");
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traceId = Guid.NewGuid().ToString("N");
        var userMessageId = Guid.NewGuid().ToString("N");
        var safeAttachments = attachments.ToArray();
        var mentionedAgentIds = mentions
            .Select(t => t.Agent.Id)
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var assistantDeltaCount = 0;
        var assistantImageSortOrder = 0;
        var assistantMessageId = Guid.NewGuid().ToString("N");
        var currentSessionId = turnSession.StateBag.GetValue<string>("sessionid") ?? string.Empty;
        var reasoningProcessId = $"reasoning-{turnId}";
        var hasReasoning = false;
        var participatingOutputChannels = new HashSet<IAiOutputChannel>();
        var turnContext = new ChatTurnContext(turnId, turnAgent, turnSession, streamCts);
        turnRegistry.Register(turnContext);

        try
        {
            ChatSessionService.ApplySelectionState(turnSession, turnProvider, turnAgentEntity, turnModel);
            turnSession.StateBag.SetValue("turnid", turnId);
            turnSession.StateBag.SetValue("traceid", traceId);
            turnSession.StateBag.SetValue("usermessageid", userMessageId);
            turnSession.StateBag.SetValue("currenttask", userInput ?? string.Empty);
            ApplyOrchestrationState(turnSession, orchestrationResult);

            var conversationMetadata = createConversationEventMetadata(
                currentSessionId,
                turnId,
                traceId,
                userMessageId,
                assistantMessageId,
                turnProvider,
                turnAgentEntity,
                turnModel,
                orchestrationResult);
            turnContext.Metadata = conversationMetadata;
            conversationEventPublisher.PublishTurnStarted(conversationMetadata, safeAttachments.Length, mentionedAgentIds);

            var contents = new List<AIContent>();
            if (!string.IsNullOrWhiteSpace(userInput))
            {
                contents.Add(new TextContent(userInput));
            }

            if (safeAttachments.Length > 0)
            {
                await attachmentLoader.LoadAsync(
                    contents,
                    safeAttachments,
                    currentSessionId,
                    userMessageId,
                    streamCts.Token).ConfigureAwait(false);
            }

            if (contents.Count == 0)
            {
                logger.LogWarning("没有有效的消息内容可发送");
                return;
            }

            var msg = new AIChatMessage
            {
                Role = ChatRole.User,
                CreatedAt = DateTimeOffset.UtcNow,
                Contents = contents,
                AuthorName = "用户",
                MessageId = userMessageId
            };

            saveUserMessage(msg, turnSession, turnAgentEntity, turnModel);
            conversationEventPublisher.PublishUserMessage(conversationMetadata, msg.Text, safeAttachments);
            turnSession.StateBag.SetValue("assistantmessageid", assistantMessageId);

            await foreach (var chunk in turnAgent.RunStreamingAsync(msg, turnSession, cancellationToken: streamCts.Token))
            {
                await realtimeEventPublisher.PublishProcessEventsAsync(turnId, reasoningProcessId, chunk.Contents, streamCts.Token).ConfigureAwait(false);
                hasReasoning |= chunk.Contents.Any(static t => t is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text));

                if (chunk.Role is not null && chunk.Role != ChatRole.Assistant)
                {
                    continue;
                }

                foreach (var content in chunk.Contents)
                {
                    if (content is not DataContent dataContent) continue;
                    if (!IsImageMimeType(dataContent.MediaType)) continue;
                    if (dataContent.Data.Length == 0) continue;

                    try
                    {
                        var ext = dataContent.MediaType switch
                        {
                            "image/png" => ".png",
                            "image/jpeg" or "image/jpg" => ".jpg",
                            "image/gif" => ".gif",
                            "image/webp" => ".webp",
                            "image/bmp" => ".bmp",
                            _ => ".png"
                        };
                        var imgName = $"ai-image-{assistantImageSortOrder + 1}{ext}";
                        var relativePath = Path.Combine("histories", currentSessionId, "images", assistantMessageId, imgName);
                        var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, relativePath);
                        var targetDir = Path.GetDirectoryName(absolutePath)!;
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        File.WriteAllBytes(absolutePath, dataContent.Data.ToArray());
                        var sha256 = ComputeFileSha256(absolutePath);
                        assetService.BatchInsert([
                            new ChatMessageAssetEntity
                            {
                                SessionId = currentSessionId,
                                MessageId = assistantMessageId,
                                Role = "assistant",
                                AssetGroup = "images",
                                AssetKind = "generated",
                                MimeType = dataContent.MediaType,
                                OriginalName = imgName,
                                RelativePath = relativePath,
                                FileSizeBytes = dataContent.Data.Length,
                                Sha256 = sha256,
                                SortOrder = assistantImageSortOrder++,
                                SourceType = "ai",
                            }
                        ]);

                        var imgMarkdown = $"\n![{imgName}]({ToFileUri(absolutePath)})\n";
                        fullResponse.Append(imgMarkdown);
                        assistantDeltaCount++;
                        conversationEventPublisher.PublishAssistantDelta(conversationMetadata, imgMarkdown, assistantDeltaCount);
                        foreach (var channel in outputChannels)
                        {
                            if (!channel.IsActive) continue;
                            participatingOutputChannels.Add(channel);
                            try
                            {
                                await channel.OnTokenAsync(turnId, imgMarkdown, currentSessionId, streamCts.Token).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "输出通道 {Channel} 处理 AI 图片 token 失败", channel.Name);
                            }
                        }

                        logger.LogInformation("AI 返回图片已保存：{Name}（{MimeType}，{Bytes} bytes）",
                            imgName, dataContent.MediaType, dataContent.Data.Length);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "保存 AI 返回图片失败");
                    }
                }

                if (string.IsNullOrEmpty(chunk.Text))
                {
                    continue;
                }

                fullResponse.Append(chunk.Text);
                assistantDeltaCount++;
                conversationEventPublisher.PublishAssistantDelta(conversationMetadata, chunk.Text, assistantDeltaCount);

                foreach (var channel in outputChannels)
                {
                    if (!channel.IsActive) continue;
                    participatingOutputChannels.Add(channel);
                    try
                    {
                        await channel.OnTokenAsync(turnId, chunk.Text, currentSessionId, streamCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "输出通道 {Channel} 处理 token 失败", channel.Name);
                    }
                }
            }

            if (hasReasoning)
            {
                await realtimeEventPublisher.PublishProcessEventAsync(
                    turnId,
                    reasoningProcessId,
                    "thinking",
                    "思考过程",
                    "success",
                    string.Empty,
                    null,
                    0,
                    streamCts.Token).ConfigureAwait(false);
            }

            AddCurrentlyActiveOutputChannels(participatingOutputChannels);
            foreach (var channel in participatingOutputChannels)
            {
                try
                {
                    await channel.OnDoneAsync(turnId, currentSessionId, streamCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "输出通道 {Channel} 处理完成事件失败", channel.Name);
                }
            }

            publishTurnCompletedOnce(
                turnContext,
                ConversationTurnStatus.Succeeded,
                userInput ?? string.Empty,
                fullResponse.ToString(),
                null,
                assistantDeltaCount,
                safeAttachments.Length);

            if (!string.IsNullOrWhiteSpace(fullResponse.ToString()))
            {
                logger.LogInformation("AI 对话完成，回复长度：{Length}", fullResponse.Length);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("AI 流式响应被取消");
            await notifyChannelsCancelledAsync(turnContext, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(fullResponse.ToString()))
            {
                var modelName = turnSession.StateBag.GetValue<string>("modelid") ?? turnModel.Name;
                await chatMessagePersistence.SavePartialResponseAsync(
                    fullResponse.ToString(),
                    turnSession,
                    turnAgent,
                    modelName,
                    assistantMessageId).ConfigureAwait(false);
            }

            publishTurnCompletedOnce(
                turnContext,
                ConversationTurnStatus.Cancelled,
                userInput,
                fullResponse.ToString(),
                null,
                assistantDeltaCount,
                safeAttachments.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI 流式响应出错");
            publishTurnCompletedOnce(
                turnContext,
                ConversationTurnStatus.Failed,
                userInput,
                fullResponse.ToString(),
                ex.Message,
                assistantDeltaCount,
                safeAttachments.Length);

            AddCurrentlyActiveOutputChannels(participatingOutputChannels);
            logger.LogError(ex, "分发错误到输出通道：{Count}", participatingOutputChannels.Count);
            foreach (var channel in participatingOutputChannels)
            {
                try
                {
                    logger.LogWarning("通知输出通道错误：{Channel}", channel.Name);
                    await channel.OnErrorAsync(turnId, $"AI 对话出错：{ex.Message}").ConfigureAwait(false);
                }
                catch (Exception chEx)
                {
                    logger.LogWarning(chEx, "输出通道 {Channel} 错误通知失败", channel.Name);
                }
            }
        }
        finally
        {
            _ = turnRegistry.ClearCurrentTurn(turnId);
            streamCts.Dispose();

            if (mentions.Count > 0)
            {
                invalidateCurrentAgent();
            }

            publisher.Publish(Events.OnAiCompleted, new VoiceSignalArgs());
        }

        void AddCurrentlyActiveOutputChannels(HashSet<IAiOutputChannel> channels)
        {
            foreach (var channel in outputChannels)
            {
                if (channel.IsActive)
                {
                    channels.Add(channel);
                }
            }
        }
    }

    private static bool IsImageMimeType(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static string ToFileUri(string absolutePath) =>
        new Uri(Path.GetFullPath(absolutePath)).AbsoluteUri;

    private void ApplyOrchestrationState(
        AgentSession turnSession,
        AgentOrchestrationResult? orchestrationResult)
    {
        if (orchestrationResult is null)
        {
            return;
        }

        turnSession.StateBag.SetValue("orchestrationmode", orchestrationResult.Mode.ToString());
        turnSession.StateBag.SetValue("orchestrationagents", string.Join(",", orchestrationResult.UsedAgentIds));
        turnSession.StateBag.SetValue("orchestrationwarnings", string.Join(" | ", orchestrationResult.Warnings));

        if (orchestrationResult.Mode != AgentOrchestrationMode.None || orchestrationResult.Warnings.Count > 0)
        {
            logger.LogInformation(
                "Turn {TurnId} 编排已应用。Mode={Mode}，Agents={Agents}，Warnings={WarningCount}",
                turnSession.StateBag.GetValue<string>("turnid") ?? string.Empty,
                orchestrationResult.Mode,
                orchestrationResult.UsedAgentIds.Count == 0 ? "无" : string.Join(", ", orchestrationResult.UsedAgentIds),
                orchestrationResult.Warnings.Count);
        }
    }
}
