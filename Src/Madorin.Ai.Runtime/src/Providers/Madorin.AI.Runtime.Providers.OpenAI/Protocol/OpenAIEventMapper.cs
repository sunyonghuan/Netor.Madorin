using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.OpenAI.Protocol;

/// <summary>
/// Converts MEAI <see cref="AgentResponseUpdate"/> stream events into the
/// runtime's canonical <see cref="RuntimeProviderEvent"/> events.
/// </summary>
public static class OpenAIEventMapper
{
    /// <summary>
    /// Maps one <see cref="AgentResponseUpdate"/> to zero or more
    /// <see cref="RuntimeProviderEvent"/> objects.
    /// </summary>
    public static IEnumerable<RuntimeProviderEvent> Map(
        string invocationId,
        AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning
                    when !string.IsNullOrEmpty(reasoning.Text):
                    yield return new ReasoningDeltaProviderEvent(
                        invocationId, reasoning.Text);
                    break;

                case TextContent text
                    when !string.IsNullOrEmpty(text.Text):
                    yield return new TextDeltaProviderEvent(
                        invocationId, text.Text);
                    break;

                case FunctionCallContent toolCall:
                    if (!string.IsNullOrEmpty(toolCall.CallId))
                    {
                        var argsJson = SerializeArguments(toolCall.Arguments);
                        yield return new ToolCallCompleteProviderEvent(
                            invocationId,
                            toolCall.CallId,
                            toolCall.Name ?? string.Empty,
                            toolCall.Name ?? string.Empty,
                            argsJson);
                    }
                    break;

                case UsageContent usage when usage.Details is not null:
                    yield return new UsageUpdatedProviderEvent(
                        invocationId,
                        (int)(usage.Details.InputTokenCount ?? 0),
                        (int)(usage.Details.OutputTokenCount ?? 0));
                    break;
            }
        }
    }

    /// <summary>
    /// Builds a stable <see cref="RuntimeError"/> from a provider exception.
    /// </summary>
    public static RuntimeError ToRuntimeError(Exception exception)
    {
        var statusCode = exception switch
        {
            System.ClientModel.ClientResultException cre when cre.Status > 0 => cre.Status,
            System.Net.Http.HttpRequestException { StatusCode: { } s } => (int)s,
            _ => (int?)null
        };
        var providerCode = statusCode?.ToString(CultureInfo.InvariantCulture)
            ?? exception.GetType().Name;

        var code = statusCode switch
        {
            401 or 403 => Contracts.RuntimeErrorCodes.AuthenticationFailed,
            429 => "ProviderRateLimited",
            _ => "ProviderRequestFailed"
        };
        var category = statusCode switch
        {
            401 or 403 => "authentication",
            429 => "rate_limit",
            _ => "provider"
        };
        var isRetryable = statusCode is 408 or 429 or >= 500
            || exception is System.Net.Http.HttpRequestException
            || exception is TimeoutException;

        using var details = System.Text.Json.JsonDocument.Parse(
            $"{{\"code\":\"{System.Text.Json.JsonEncodedText.Encode(providerCode)}\"}}");

        return new RuntimeError(
            code,
            category,
            $"OpenAI request failed: {providerCode}",
            isRetryable,
            details.RootElement.Clone(),
            Guid.NewGuid().ToString("N"));
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null or { Count: 0 })
        {
            return "{}";
        }

        var buf = new StringBuilder("{");
        var first = true;
        foreach (var kv in arguments)
        {
            if (!first)
            {
                buf.Append(',');
            }

            first = false;
            buf.Append('"');
            buf.Append(kv.Key
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal));
            buf.Append("\":");

            if (kv.Value is JsonElement je)
            {
                buf.Append(je.GetRawText());
            }
            else
            {
                var str = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                buf.Append('"');
                buf.Append(str
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal));
                buf.Append('"');
            }
        }

        buf.Append('}');
        return buf.ToString();
    }
}
