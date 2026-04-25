using Microsoft.Extensions.Logging;

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Netor.Cortana.AI.Drivers;

internal class DeepseekOverrideHandler(ILogger<DeepseekOverrideHandler> logger) : DelegatingHandler
{
    private readonly ILogger<DeepseekOverrideHandler> _logger = logger;

    /// <summary>
    /// 拦截 DeepSeek 请求，在最终 JSON 中补写 reasoning_content。
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Content is not null && IsJsonContent(request.Content.Headers.ContentType))
            {
                var originalBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var rewrittenBody = RewriteRequestBody(originalBody);
                if (!ReferenceEquals(rewrittenBody, originalBody))
                {
                    request.Content = CreateJsonContent(rewrittenBody, request.Content.Headers.ContentType);
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("DeepSeek 请求已取消。");
            throw;
        }
    }

    /// <summary>
    /// 在聊天请求体中为所有 assistant 消息补写 reasoning_content，最后一条带 tool_calls 的 assistant 写入真实 reasoning，其余写空字符串。
    /// </summary>
    private static string RewriteRequestBody(string originalBody)
    {
        if (string.IsNullOrWhiteSpace(originalBody))
        {
            return originalBody;
        }

        var reasoning = DeepseekDelegatingChatClient.CurrentReplayContext?.ReasoningContent;
        if (string.IsNullOrWhiteSpace(reasoning))
            reasoning = string.Empty;

        using var document = JsonDocument.Parse(originalBody);
        if (!document.RootElement.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return originalBody;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            var injected = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("messages"))
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();

                    var lastAssistantIndex = FindLastAssistantToolCallIndex(messages);
                    var index = 0;
                    foreach (var message in messages.EnumerateArray())
                    {
                        if (IsAssistantMessage(message))
                        {
                            WriteAssistantMessageWithReasoning(writer, message, index == lastAssistantIndex ? reasoning : string.Empty);
                            injected = true;
                        }
                        else
                        {
                            message.WriteTo(writer);
                        }

                        index++;
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();

            if (!injected)
            {
                return originalBody;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static int FindLastAssistantToolCallIndex(JsonElement messages)
    {
        var targetIndex = -1;
        var index = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("role", out var role)
                && role.ValueKind == JsonValueKind.String
                && string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase)
                && message.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                targetIndex = index;
            }

            index++;
        }

        return targetIndex;
    }

    private static bool IsAssistantMessage(JsonElement message)
    {
        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAssistantMessageWithReasoning(Utf8JsonWriter writer, JsonElement message, string reasoning)
    {
        writer.WriteStartObject();

        var hasReasoning = false;
        foreach (var property in message.EnumerateObject())
        {
            property.WriteTo(writer);
            if (property.NameEquals("reasoning_content"))
            {
                hasReasoning = true;
            }
        }

        if (!hasReasoning)
        {
            writer.WriteString("reasoning_content", reasoning);
        }

        writer.WriteEndObject();
    }

    private static bool IsJsonContent(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        return !string.IsNullOrWhiteSpace(mediaType)
            && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static StringContent CreateJsonContent(string body, MediaTypeHeaderValue? originalContentType)
    {
        var mediaType = originalContentType?.MediaType ?? "application/json";
        var content = new StringContent(body, Encoding.UTF8, mediaType);

        if (originalContentType?.CharSet is { Length: > 0 } charSet)
        {
            content.Headers.ContentType!.CharSet = charSet;
        }

        return content;
    }
}