using System.Text;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record ProviderTokenEstimate(
    int? InputTokens,
    int? OutputTokens,
    ProviderUsageAccuracy Accuracy);

public static class ProviderTokenEstimator
{
    public static ProviderTokenEstimate Estimate(RuntimeProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        long bytes = 0;
        foreach (var message in request.Messages)
        {
            bytes += Encoding.UTF8.GetByteCount(message.Role);
            foreach (var block in message.Content)
            {
                bytes += CountBytes(block);
            }
        }

        if (request.Tools is not null)
        {
            foreach (var tool in request.Tools)
            {
                bytes += Encoding.UTF8.GetByteCount(tool.ToolId);
                bytes += Encoding.UTF8.GetByteCount(tool.DisplayName);
                bytes += Encoding.UTF8.GetByteCount(tool.Description);
                bytes += Encoding.UTF8.GetByteCount(tool.InputSchemaJson);
            }
        }

        var estimatedTokens = (int)Math.Clamp((bytes + 3) / 4, 0, int.MaxValue);
        return new ProviderTokenEstimate(estimatedTokens, null, ProviderUsageAccuracy.Estimated);
    }

    private static long CountBytes(ContentBlock block) => block switch
    {
        TextContentBlock text => Encoding.UTF8.GetByteCount(text.Text),
        ReasoningContentBlock reasoning => Encoding.UTF8.GetByteCount(reasoning.Content),
        ToolCallContentBlock toolCall => Encoding.UTF8.GetByteCount(toolCall.Arguments.GetRawText()),
        ToolResultContentBlock toolResult => toolResult.Content.Sum(CountBytes),
        BlobRefContentBlock => 0,
        _ => 0
    };
}
