using Netor.Cortana.Entitys;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// DeepSeek OpenAI 兼容协议推理内容回传适配器。
/// </summary>
public static class DeepSeekReasoningRequestRewriter
{
    public static bool IsDeepSeekProvider(AiProviderEntity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return string.Equals(provider.ProviderType, "Deepseek", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.ProviderType, "DeepSeek", StringComparison.OrdinalIgnoreCase)
            || provider.Url.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }

    public static void RewriteRequest(JsonObject root, string replayReasoning)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root["messages"] is not JsonArray messages)
        {
            return;
        }

        var lastAssistantToolCallIndex = FindLastAssistantToolCallIndex(messages);
        var fallbackReasoning = string.IsNullOrWhiteSpace(replayReasoning) ? string.Empty : replayReasoning;

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message || !IsAssistantMessage(message))
            {
                continue;
            }

            if (TryGetString(message["reasoning_content"], out _))
            {
                continue;
            }

            var reasoning = TryGetString(message["reasoning"], out var inlineReasoning)
                ? inlineReasoning
                : index == lastAssistantToolCallIndex ? fallbackReasoning : string.Empty;

            InsertStringAfterContent(message, "reasoning_content", reasoning);
        }
    }

    public static string ExtractReasoningFromResponse(string contentType, byte[] responseBody)
    {
        if (responseBody.Length == 0)
        {
            return string.Empty;
        }

        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractReasoningFromSse(responseBody);
        }

        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractReasoningFromJson(responseBody);
        }

        return string.Empty;
    }

    private static int FindLastAssistantToolCallIndex(JsonArray messages)
    {
        var targetIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is JsonObject message && IsAssistantMessage(message) && HasToolCalls(message))
            {
                targetIndex = index;
            }
        }

        return targetIndex;
    }

    private static bool IsAssistantMessage(JsonObject message)
    {
        return TryGetString(message["role"], out var role)
            && string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasToolCalls(JsonObject message)
    {
        return message["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0;
    }

    private static void InsertStringAfterContent(JsonObject message, string propertyName, string value)
    {
        var rewritten = new JsonObject();
        var inserted = false;

        foreach (var property in message.ToList())
        {
            rewritten[property.Key] = property.Value?.DeepClone();

            if (!inserted && string.Equals(property.Key, "content", StringComparison.OrdinalIgnoreCase))
            {
                rewritten[propertyName] = value;
                inserted = true;
            }
        }

        if (!inserted)
        {
            rewritten[propertyName] = value;
        }

        message.Clear();
        foreach (var property in rewritten)
        {
            message[property.Key] = property.Value?.DeepClone();
        }
    }

    private static string ExtractReasoningFromJson(byte[] responseBody)
    {
        try
        {
            var node = JsonNode.Parse(responseBody)?.AsObject();
            if (node?["choices"] is not JsonArray choices)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var choice in choices.OfType<JsonObject>())
            {
                if (choice["message"] is not JsonObject message)
                {
                    continue;
                }

                AppendReasoning(builder, message["reasoning_content"]);
                AppendReasoning(builder, message["reasoning"]);
            }

            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractReasoningFromSse(byte[] responseBody)
    {
        var builder = new StringBuilder();
        var text = Encoding.UTF8.GetString(responseBody);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(payload)?.AsObject();
                if (node?["choices"] is not JsonArray choices)
                {
                    continue;
                }

                foreach (var choice in choices.OfType<JsonObject>())
                {
                    if (choice["delta"] is not JsonObject delta)
                    {
                        continue;
                    }

                    AppendReasoning(builder, delta["reasoning_content"], separator: string.Empty);
                    AppendReasoning(builder, delta["reasoning"], separator: string.Empty);
                }
            }
            catch
            {
                // 忽略单个 SSE chunk 的解析失败。
            }
        }

        return builder.ToString();
    }

    private static void AppendReasoning(StringBuilder builder, JsonNode? node, string separator = "\n\n")
    {
        if (!TryGetString(node, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0 && separator.Length > 0)
        {
            builder.Append(separator);
        }

        builder.Append(text);
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<string>() ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
