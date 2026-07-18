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
/// 承接视频生成 turn 的执行与事件桥接。
/// </summary>
public sealed class ChatVideoTurnExecutor(
    AiProviderDriverRegistry driverRegistry,
    ChatGenerationContextBuilder generationContextBuilder,
    ChatGenerationTurnCoordinator generationTurnCoordinator,
    ChatConversationEventPublisher conversationEventPublisher,
    ChatMessageAssetService assetService,
    IAppPaths appPaths,
    IEnumerable<IAiOutputChannel> outputChannels,
    ILogger<ChatVideoTurnExecutor> logger)
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
            "video",
            "视频生成任务已排队，正在等待厂商返回结果。",
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
            var generationPrompt = generationContextBuilder.BuildPrompt(prompt, state.CurrentSessionId, "视频", turnAgentEntity);
            turnSession.StateBag.SetValue("currenttask", generationPrompt);
            await generationTurnCoordinator.PublishQueuedAsync(state, state.StreamCts.Token).ConfigureAwait(false);

            var driver = driverRegistry.Resolve(turnProvider);
            if (!driver.SupportsVideoGeneration(turnProvider, turnModel))
            {
                throw new NotSupportedException($"当前厂商驱动 '{driver.Definition.DisplayName}' 不支持视频生成端点。");
            }

            await generationTurnCoordinator.PublishQueuedAsync(
                state with { QueuedMessage = "视频生成中，已合并当前智能体与最近对话上下文。" },
                state.StreamCts.Token).ConfigureAwait(false);

            var generated = await driver.GenerateVideosAsync(
                new ProviderVideoGenerationRequest(turnProvider, turnModel, generationPrompt),
                state.StreamCts.Token).ConfigureAwait(false);
            if (generated.Count == 0)
            {
                throw new InvalidOperationException("视频生成接口未返回可保存的视频。");
            }

            var assetEntities = new List<ChatMessageAssetEntity>(generated.Count);
            for (var index = 0; index < generated.Count; index++)
            {
                var video = generated[index];
                var fileName = string.IsNullOrWhiteSpace(video.FileName)
                    ? $"generated-video-{index + 1}.mp4"
                    : video.FileName;
                var relativePath = Path.Combine("histories", state.CurrentSessionId, "video", state.AssistantMessageId, fileName);
                var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, relativePath);
                var targetDir = Path.GetDirectoryName(absolutePath)!;
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await File.WriteAllBytesAsync(absolutePath, video.Data, state.StreamCts.Token).ConfigureAwait(false);
                assetEntities.Add(new ChatMessageAssetEntity
                {
                    SessionId = state.CurrentSessionId,
                    MessageId = state.AssistantMessageId,
                    Role = "assistant",
                    AssetGroup = "video",
                    AssetKind = "generated",
                    MimeType = string.IsNullOrWhiteSpace(video.MimeType) ? "video/mp4" : video.MimeType,
                    OriginalName = fileName,
                    RelativePath = relativePath,
                    FileSizeBytes = video.Data.LongLength,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(video.Data)),
                    SortOrder = index,
                    SourceType = "generated",
                });

                responseMarkdown.AppendLine($"[{fileName}（{FormatFileSize(video.Data.LongLength)}）]({ToFileUri(absolutePath)})");
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
                $"视频生成完成：{generated.Count} 个视频。",
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
            logger.LogError(ex, "视频生成失败");
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

                await channel.OnErrorAsync(state.TurnId, $"视频生成失败：{ex.Message}").ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            generationTurnCoordinator.CompleteTurn(state);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:F1} KB";
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }

        return $"{bytes / 1024d / 1024d / 1024d:F1} GB";
    }

    private static string ToFileUri(string absolutePath) =>
        new Uri(Path.GetFullPath(absolutePath)).AbsoluteUri;
}
