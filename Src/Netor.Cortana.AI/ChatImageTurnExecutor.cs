using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Security.Cryptography;
using System.Text;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI;

/// <summary>
/// 承接图片生成 turn 的执行与事件桥接。
/// </summary>
public sealed class ChatImageTurnExecutor(
    AiProviderDriverRegistry driverRegistry,
    ChatGenerationContextBuilder generationContextBuilder,
    ChatGenerationTurnCoordinator generationTurnCoordinator,
    ChatConversationEventPublisher conversationEventPublisher,
    ChatMessageAssetService assetService,
    IAppPaths appPaths,
    IEnumerable<IAiOutputChannel> outputChannels,
    ILogger<ChatImageTurnExecutor> logger)
{
    public async Task ExecuteAsync(
        string prompt,
        AIAgent turnAgent,
        AgentSession turnSession,
        AiProviderEntity turnProvider,
        AgentEntity turnAgentEntity,
        AiModelEntity turnModel,
        Action<AIChatMessage, AgentSession, AgentEntity, AiModelEntity> saveUserMessage,
        Action<string, string, string, AgentSession, AgentEntity, string> saveAssistantMessage,
        Action<ChatTurnContext?, ConversationTurnStatus, string, string, string?, int, int> publishTurnCompletedOnce,
        Func<string, string, string, string, string, AiProviderEntity, AgentEntity, AiModelEntity, AgentOrchestrationResult?, ConversationEventMetadata> createConversationEventMetadata,
        Func<ChatTurnContext?, CancellationToken, Task> notifyChannelsCancelledAsync,
        CancellationToken cancellationToken)
    {
        var responseMarkdown = new StringBuilder();
        var deltaCount = 0;
        var state = generationTurnCoordinator.Start(
            prompt,
            "image",
            "图片生成中，已合并当前智能体与最近对话上下文。",
            turnAgent,
            turnSession,
            turnProvider,
            turnAgentEntity,
            turnModel,
            saveUserMessage,
            createConversationEventMetadata,
            cancellationToken);

        try
        {
            var generationPrompt = generationContextBuilder.BuildPrompt(prompt, state.CurrentSessionId, "图片", turnAgentEntity);
            turnSession.StateBag.SetValue("currenttask", generationPrompt);
            await generationTurnCoordinator.PublishQueuedAsync(state, state.StreamCts.Token).ConfigureAwait(false);

            var driver = driverRegistry.Resolve(turnProvider);
            if (!driver.SupportsImageGeneration(turnProvider, turnModel))
            {
                throw new NotSupportedException($"当前厂商驱动 '{driver.Definition.DisplayName}' 不支持图片生成端点。");
            }

            var generated = await driver.GenerateImagesAsync(
                new ProviderImageGenerationRequest(turnProvider, turnModel, generationPrompt),
                state.StreamCts.Token).ConfigureAwait(false);
            if (generated.Count == 0)
            {
                throw new InvalidOperationException("图片生成接口未返回可保存的图片。");
            }

            var assetEntities = new List<ChatMessageAssetEntity>(generated.Count);
            for (var index = 0; index < generated.Count; index++)
            {
                var image = generated[index];
                var fileName = string.IsNullOrWhiteSpace(image.FileName)
                    ? $"generated-image-{index + 1}.png"
                    : image.FileName;
                var relativePath = Path.Combine("histories", state.CurrentSessionId, "images", state.AssistantMessageId, fileName);
                var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, relativePath);
                var targetDir = Path.GetDirectoryName(absolutePath)!;
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await File.WriteAllBytesAsync(absolutePath, image.Data, state.StreamCts.Token).ConfigureAwait(false);
                assetEntities.Add(new ChatMessageAssetEntity
                {
                    SessionId = state.CurrentSessionId,
                    MessageId = state.AssistantMessageId,
                    Role = "assistant",
                    AssetGroup = "images",
                    AssetKind = "generated",
                    MimeType = string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType,
                    OriginalName = fileName,
                    RelativePath = relativePath,
                    FileSizeBytes = image.Data.LongLength,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(image.Data)),
                    SortOrder = index,
                    SourceType = "generated",
                });

                responseMarkdown.AppendLine($"![{fileName}]({ToFileUri(absolutePath)})");
            }

            assetService.BatchInsert(assetEntities);
            var assistantText = responseMarkdown.ToString().TrimEnd();
            saveAssistantMessage(
                state.AssistantMessageId,
                state.CurrentSessionId,
                assistantText,
                turnSession,
                turnAgentEntity,
                turnModel.Name);

            foreach (var channel in outputChannels)
            {
                if (!channel.IsActive)
                {
                    continue;
                }

                await channel.OnTokenAsync(state.TurnId, assistantText, state.CurrentSessionId, state.StreamCts.Token).ConfigureAwait(false);
                await channel.OnDoneAsync(state.TurnId, state.CurrentSessionId, state.StreamCts.Token).ConfigureAwait(false);
            }

            deltaCount = generated.Count;
            conversationEventPublisher.PublishAssistantDelta(state.Metadata, assistantText, deltaCount);
            await generationTurnCoordinator.PublishSucceededAsync(
                state,
                $"图片生成完成：{generated.Count} 张图片。",
                state.StreamCts.Token).ConfigureAwait(false);
            publishTurnCompletedOnce(
                state.TurnContext,
                ConversationTurnStatus.Succeeded,
                prompt,
                assistantText,
                null,
                deltaCount,
                0);
        }
        catch (OperationCanceledException) when (state.StreamCts.IsCancellationRequested)
        {
            await generationTurnCoordinator.PublishCancelledAsync(state).ConfigureAwait(false);
            await notifyChannelsCancelledAsync(state.TurnContext, CancellationToken.None).ConfigureAwait(false);
            publishTurnCompletedOnce(
                state.TurnContext,
                ConversationTurnStatus.Cancelled,
                prompt,
                responseMarkdown.ToString(),
                "用户取消",
                deltaCount,
                0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "图片生成失败");
            await generationTurnCoordinator.PublishFailedAsync(state, ex.Message).ConfigureAwait(false);
            publishTurnCompletedOnce(
                state.TurnContext,
                ConversationTurnStatus.Failed,
                prompt,
                responseMarkdown.ToString(),
                ex.Message,
                deltaCount,
                0);

            foreach (var channel in outputChannels)
            {
                if (!channel.IsActive)
                {
                    continue;
                }

                await channel.OnErrorAsync(state.TurnId, $"图片生成失败：{ex.Message}").ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            generationTurnCoordinator.CompleteTurn(state);
        }
    }

    private static string ToFileUri(string absolutePath) =>
        new Uri(Path.GetFullPath(absolutePath)).AbsoluteUri;
}
