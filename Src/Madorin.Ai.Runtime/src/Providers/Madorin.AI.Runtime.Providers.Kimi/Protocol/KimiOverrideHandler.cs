using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Madorin.AI.Runtime.Providers.Kimi.Protocol;

/// <summary>
/// 拦截 Kimi 请求，执行两项协议补丁：
/// 1. 为携带 tool_calls 的 assistant 消息补写 <c>"partial": true</c>（Kimi Partial Mode 要求）；
/// 2. 在顶层注入 <c>thinking</c> 参数（启用 Kimi 深度思考模式）；
/// 3. 清洗 content 数组中空 text 部分（避免 Kimi API 拒绝请求）。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Kimi/KimiOverrideHandler.cs</c> 迁移。
/// </summary>
internal sealed partial class KimiOverrideHandler(ILogger<KimiOverrideHandler> logger) : DelegatingHandler
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
                var originalBody = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

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
            LogRequestCancelled();
            throw;
        }
    }

    private static string RewriteRequestBody(string originalBody)
    {
        if (string.IsNullOrWhiteSpace(originalBody))
        {
            return originalBody;
        }

        using var document = JsonDocument.Parse(originalBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return originalBody;
        }

        var needsPartialInjection = NeedsPartialInjection(messages);
        var needsContentSanitization = NeedsContentSanitization(messages);
        var hasThinking = root.TryGetProperty("thinking", out _);

        if (hasThinking && !needsPartialInjection && !needsContentSanitization)
        {
            return originalBody;
        }

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
                        {
                            WriteMessage(writer, message, injectPartial: true);
                        }
                        else if (HasEmptyTextContentParts(message))
                        {
                            WriteMessage(writer, message, injectPartial: false);
                        }
                        else
                        {
                            message.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            // 在顶层补写 thinking（未携带时注入）
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
            if (IsAssistantWithToolCalls(message) && !message.TryGetProperty("partial", out _))
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
            {
                hasPartial = true;
            }
        }

        if (injectPartial && !hasPartial)
        {
            writer.WriteBoolean("partial", true);
        }

        writer.WriteEndObject();
    }

    private static void WriteSanitizedContent(
        Utf8JsonWriter writer, JsonElement content, bool allowNullWhenEmpty)
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
        {
            content.Headers.ContentType!.CharSet = charSet;
        }

        return content;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kimi 请求已取消。")]
    private partial void LogRequestCancelled();
}
