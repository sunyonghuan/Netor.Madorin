using Microsoft.Extensions.AI;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// DeepSeek 专用聊天客户端包装器，用于在 HTTP 发送前捕获最近一条 assistant 的 reasoning。
/// </summary>
internal sealed class DeepseekDelegatingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private static readonly AsyncLocal<DeepseekReplayContext?> CurrentContext = new();

    /// <summary>
    /// 当前请求的 DeepSeek 回传上下文。
    /// </summary>
    internal sealed class DeepseekReplayContext
    {
        /// <summary>
        /// 最近一条 assistant 消息中的完整 reasoning 文本。
        /// </summary>
        public string? ReasoningContent { get; init; }
    }

    /// <summary>
    /// 获取当前异步上下文中缓存的回传上下文。
    /// </summary>
    public static DeepseekReplayContext? CurrentReplayContext => CurrentContext.Value;

    /// <summary>
    /// 获取非流式响应前，先缓存待回传的 reasoning。
    /// </summary>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CurrentContext.Value = ExtractReplayContext(messages);
        return ExecuteAsync(messages, options, cancellationToken);
    }

    /// <summary>
    /// 获取流式响应前，先缓存待回传的 reasoning。
    /// </summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CurrentContext.Value = ExtractReplayContext(messages);
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            CurrentContext.Value = null;
        }
    }

    private async Task<ChatResponse> ExecuteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CurrentContext.Value = null;
        }
    }

    /// <summary>
    /// 从消息集合中提取最后一条 assistant 消息的回传上下文。
    /// </summary>
    /// <param name="messages">待发送消息集合。</param>
    /// <returns>最近一条 assistant 的回传上下文。</returns>
    private static DeepseekReplayContext? ExtractReplayContext(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        ChatMessage? lastAssistantMessage = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                lastAssistantMessage = message;
            }
        }

        if (lastAssistantMessage?.Contents is null)
        {
            return null;
        }

        foreach (var content in lastAssistantMessage.Contents)
        {
            if (content is TextReasoningContent textReasoning && !string.IsNullOrWhiteSpace(textReasoning.Text))
            {
                return new DeepseekReplayContext
                {
                    ReasoningContent = textReasoning.Text
                };
            }
        }

        return null;
    }
}
