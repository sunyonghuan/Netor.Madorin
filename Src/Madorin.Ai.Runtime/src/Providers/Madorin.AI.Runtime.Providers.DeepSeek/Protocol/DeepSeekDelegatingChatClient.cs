using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Madorin.AI.Runtime.Providers.DeepSeek.Protocol;

/// <summary>
/// DeepSeek 专用聊天客户端包装器。
/// 在发送请求前缓存最近一条 assistant 消息的 reasoning 内容，
/// 以便 <see cref="DeepSeekOverrideHandler"/> 在 HTTP 层补写 reasoning_content。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Deepseek/DeepseekDelegatingChatClient.cs</c> 迁移。
/// </summary>
internal sealed class DeepSeekDelegatingChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        DeepSeekReasoningContext.SetReasoning(ExtractLastReasoning(messages));
        return ExecuteAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DeepSeekReasoningContext.SetReasoning(ExtractLastReasoning(messages));
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
            DeepSeekReasoningContext.Clear();
        }
    }

    private async Task<ChatResponse> ExecuteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DeepSeekReasoningContext.Clear();
        }
    }

    private static string? ExtractLastReasoning(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        ChatMessage? lastAssistant = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                lastAssistant = message;
            }
        }

        if (lastAssistant?.Contents is null)
        {
            return null;
        }

        foreach (var content in lastAssistant.Contents)
        {
            if (content is TextReasoningContent reasoning
                && !string.IsNullOrWhiteSpace(reasoning.Text))
            {
                return reasoning.Text;
            }
        }

        return null;
    }
}
