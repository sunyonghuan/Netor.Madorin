using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.OpenAI.Protocol;

/// <summary>
/// Converts between the runtime's canonical <see cref="ContentBlock"/> format
/// and the MEAI <see cref="ChatMessage"/> format expected by the OpenAI adapter.
/// </summary>
public static class OpenAIMessageMapper
{
    /// <summary>Converts canonical content blocks into MEAI chat messages.</summary>
    public static ChatMessage[] ToMessages(RuntimeProviderMessage[] source)
    {
        var messages = new List<ChatMessage>(source.Length);

        foreach (var message in source)
        {
            var content = new List<AIContent>(message.Content.Length);
            foreach (var block in message.Content)
            {
                content.Add(block switch
                {
                    TextContentBlock text => new TextContent(text.Text),
                    ReasoningContentBlock reasoning => new TextReasoningContent(reasoning.Content),
                    ToolCallContentBlock toolCall => new FunctionCallContent(
                            toolCall.CallId,
                            toolCall.Name,
                            ToArgumentDictionary(toolCall.Arguments)),
                    ToolResultContentBlock toolResult => new FunctionResultContent(
                            toolResult.CallId,
                            ToResultObject(toolResult)),
                    _ => throw new InvalidOperationException(
                        $"ContentBlock type '{block.GetType().Name}' is not supported by the OpenAI adapter.")
                });
            }

            messages.Add(new ChatMessage(ToRole(message.Role), content));
        }

        return [.. messages];
    }

    private static ChatRole ToRole(string role) => role switch
    {
        RuntimeProviderRoles.System => ChatRole.System,
        RuntimeProviderRoles.User => ChatRole.User,
        RuntimeProviderRoles.Assistant => ChatRole.Assistant,
        RuntimeProviderRoles.Tool => ChatRole.Tool,
        _ => throw new InvalidOperationException($"Unsupported Provider message role '{role}'.")
    };

    /// <summary>
    /// Converts tool descriptors into MEAI AI tools the model can call.
    /// </summary>
    public static List<AITool>? ToTools(ToolDescriptor[]? tools)
    {
        if (tools is not { Length: > 0 })
        {
            return null;
        }

        var result = new List<AITool>(tools.Length);
        foreach (var tool in tools)
        {
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            result.Add(AIFunctionFactory.CreateDeclaration(
                tool.DisplayName,
                tool.Description,
                schema.RootElement.Clone()));
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> ToArgumentDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = arguments.Clone()
            };
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            dict[property.Name] = property.Value.Clone();
        }

        return dict;
    }

    private static object ToResultObject(ToolResultContentBlock result)
    {
        if (result.Content is [TextContentBlock text])
        {
            return text.Text;
        }

        return JsonSerializer.SerializeToElement(
            result.Content,
            RuntimeJsonContext.Default.ContentBlockArray);
    }
}
