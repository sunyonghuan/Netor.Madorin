using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// OllamaSharp 在 Native AOT 下无法为内部类型 OllamaFunctionResultContent 提供稳定的源生成元数据。
/// 这里在进入 OllamaSharp 前把工具结果转换成 OllamaSharp 期望的普通文本 payload，避免触发内部类型序列化。
/// </summary>
internal sealed partial class OllamaDelegatingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(NormalizeMessages(messages), options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(NormalizeMessages(messages), options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    private static IEnumerable<ChatMessage> NormalizeMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            yield return NormalizeMessage(message);
        }
    }

    private static ChatMessage NormalizeMessage(ChatMessage message)
    {
        var contents = message.Contents;
        if (contents.Count == 0)
        {
            return message;
        }

        List<AIContent>? normalizedContents = null;
        for (var i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            var normalizedContent = NormalizeContent(content);
            if (!ReferenceEquals(normalizedContent, content) && normalizedContents is null)
            {
                normalizedContents = new List<AIContent>(contents.Count);
                for (var j = 0; j < i; j++)
                {
                    normalizedContents.Add(contents[j]);
                }
            }

            normalizedContents?.Add(normalizedContent);
        }

        if (normalizedContents is null)
        {
            return message;
        }

        return new ChatMessage(message.Role, normalizedContents)
        {
            AdditionalProperties = message.AdditionalProperties,
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation
        };
    }

    private static AIContent NormalizeContent(AIContent content)
    {
        return content is FunctionResultContent functionResult
            ? new TextContent(SerializeFunctionResult(functionResult))
            : content;
    }

    private static string SerializeFunctionResult(FunctionResultContent functionResult)
    {
        var result = JsonSerializer.SerializeToElement(
            functionResult.Result,
            OllamaFunctionResultJsonContext.Default.Options);

        var payload = new OllamaFunctionResultPayload(functionResult.CallId, result);
        return JsonSerializer.Serialize(
            payload,
            OllamaFunctionResultJsonContext.Default.OllamaFunctionResultPayload);
    }

    private sealed record OllamaFunctionResultPayload(string CallId, JsonElement Result);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(OllamaFunctionResultPayload))]
    private sealed partial class OllamaFunctionResultJsonContext : JsonSerializerContext;
}
