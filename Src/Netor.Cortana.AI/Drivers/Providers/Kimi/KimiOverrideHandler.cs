using Microsoft.Extensions.Logging;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 拦截 Kimi 请求，在顶层注入 thinking 参数（extra_body），
/// 并为携带 tool_calls 的 assistant 消息补写 "partial": true。
/// </summary>
internal class KimiOverrideHandler(ILogger<KimiOverrideHandler> logger) : DelegatingHandler
{
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
#if DEBUG
                logger.LogWarning("Kimi 请求原始内容：{originalBody}", originalBody);
#endif
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
            logger.LogDebug("Kimi 请求已取消。");
            throw;
        }
    }

    /// <summary>
    /// 重写请求体：
    /// 1. 为携带 tool_calls 的 assistant 消息补写 "partial": true（Partial Mode 要求）；
    /// 2. 在顶层补写 thinking 参数（通过 extra_body 传递的思维模式开关）。
    /// </summary>
    private static string RewriteRequestBody(string originalBody)
    {
        if (string.IsNullOrWhiteSpace(originalBody))
            return originalBody;

        using var document = JsonDocument.Parse(originalBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return originalBody;
        }

        // 检查是否需要做 partial 注入
        var needsPartialInjection = NeedsPartialInjection(messages);
        var needsContentSanitization = NeedsContentSanitization(messages);

        // 如果 thinking 已存在且不需要 partial 注入，则跳过重写
        var hasThinking = root.TryGetProperty("thinking", out _);
        if (hasThinking && !needsPartialInjection && !needsContentSanitization)
            return originalBody;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("messages"))
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();

                    foreach (var message in messages.EnumerateArray())
                    {
                        if (IsAssistantWithToolCalls(message))
                            WriteMessage(writer, message, injectPartial: true);
                        else if (HasEmptyTextContentParts(message))
                            WriteMessage(writer, message, injectPartial: false);
                        else
                            message.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            // 注入 thinking（extra_body 等效注入）—— 原始请求未携带时补写
            if (!hasThinking)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool NeedsPartialInjection(JsonElement messages)
    {
        foreach (var message in messages.EnumerateArray())
        {
            if (IsAssistantWithToolCalls(message)
                && !message.TryGetProperty("partial", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NeedsContentSanitization(JsonElement messages)
    {
        foreach (var message in messages.EnumerateArray())
        {
            if (HasEmptyTextContentParts(message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssistantWithToolCalls(JsonElement message)
    {
        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase)
            && message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array;
    }

    /// <summary>
    /// 将 assistant 消息写入输出流，并在末尾补写 "partial": true。
    /// 若原消息已含 partial 字段则直接原样输出。
    /// </summary>
    private static bool HasEmptyTextContentParts(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (IsEmptyTextContentPart(part))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmptyTextContentPart(JsonElement part)
    {
        return part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
            && part.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && string.IsNullOrWhiteSpace(text.GetString());
    }

    private static void WriteMessage(Utf8JsonWriter writer, JsonElement message, bool injectPartial)
    {
        writer.WriteStartObject();

        var hasPartial = false;
        foreach (var property in message.EnumerateObject())
        {
            if (property.NameEquals("content")
                && property.Value.ValueKind == JsonValueKind.Array
                && HasEmptyTextContentParts(message))
            {
                WriteSanitizedContent(writer, property.Value, IsAssistantWithToolCalls(message));
                continue;
            }

            property.WriteTo(writer);
            if (property.NameEquals("partial"))
                hasPartial = true;
        }

        if (injectPartial && !hasPartial)
            writer.WriteBoolean("partial", true);

        writer.WriteEndObject();
    }

    private static void WriteSanitizedContent(
        Utf8JsonWriter writer,
        JsonElement content,
        bool allowNullWhenEmpty)
    {
        using var stream = new MemoryStream();
        using (var contentWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            contentWriter.WriteStartArray();
            var hasAnyPart = false;

            foreach (var part in content.EnumerateArray())
            {
                if (IsEmptyTextContentPart(part))
                {
                    continue;
                }

                part.WriteTo(contentWriter);
                hasAnyPart = true;
            }

            contentWriter.WriteEndArray();

            if (!hasAnyPart && allowNullWhenEmpty)
            {
                writer.WriteNull("content");
                return;
            }
        }

        writer.WritePropertyName("content");
        using var document = JsonDocument.Parse(stream.ToArray());
        document.RootElement.WriteTo(writer);
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
            content.Headers.ContentType!.CharSet = charSet;

        return content;
    }
}
