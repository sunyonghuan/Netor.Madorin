using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

internal static class ContextProjectionBuilder
{
    internal static async Task<Result> BuildAsync(
        IReadOnlyList<ConversationRecordV1> history,
        int tokenLimit,
        Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> estimator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenLimit);
        var units = BuildAtomicUnits(history);
        var allMessages = units.SelectMany(static unit => unit.Messages).ToArray();
        var fullEstimate = await EstimateAsync(allMessages, estimator, ct).ConfigureAwait(false);
        if (fullEstimate.Tokens <= tokenLimit)
        {
            return new Result(
                allMessages,
                "Full",
                DroppedMessageCount: 0,
                fullEstimate.Tokens,
                fullEstimate.Source);
        }

        var selected = new List<AtomicUnit>();
        TokenEstimate? selectedEstimate = null;
        for (var index = units.Count - 1; index >= 0; index--)
        {
            var unit = units[index];
            selected.Insert(0, unit);
            var candidateMessages = selected
                .SelectMany(static candidate => candidate.Messages)
                .ToArray();
            var candidateEstimate = await EstimateAsync(candidateMessages, estimator, ct)
                .ConfigureAwait(false);
            if (selected.Count > 1 && candidateEstimate.Tokens > tokenLimit)
            {
                selected.RemoveAt(0);
                break;
            }

            selectedEstimate = candidateEstimate;
        }

        var messages = selected.SelectMany(static unit => unit.Messages).ToArray();
        var finalEstimate = selectedEstimate
            ?? await EstimateAsync(messages, estimator, ct).ConfigureAwait(false);
        return new Result(
            messages,
            "TailWindow",
            history.Count - messages.Length,
            finalEstimate.Tokens,
            finalEstimate.Source);
    }

    private static async ValueTask<TokenEstimate> EstimateAsync(
        RuntimeProviderMessage[] messages,
        Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> estimator,
        CancellationToken ct)
    {
        var estimate = await estimator(messages, ct).ConfigureAwait(false);
        if (estimate.InputTokens is { } inputTokens)
        {
            return new TokenEstimate(
                inputTokens,
                $"provider.{estimate.Accuracy.ToString().ToLowerInvariant()}");
        }

        long bytes = 0;
        foreach (var message in messages)
        {
            bytes += Encoding.UTF8.GetByteCount(message.Role);
            foreach (var block in message.Content)
            {
                bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                    block,
                    RuntimeJsonContext.Default.ContentBlock));
            }
        }

        return new TokenEstimate(
            (int)Math.Clamp((bytes + 3) / 4, 0, int.MaxValue),
            "runtime.utf8-bytes/4");
    }

    private static List<AtomicUnit> BuildAtomicUnits(
        IReadOnlyList<ConversationRecordV1> history)
    {
        var messages = history.Select(CreateProjectedMessage).ToArray();
        var units = new List<AtomicUnit>(messages.Length);
        for (var index = 0; index < messages.Length; index++)
        {
            var current = messages[index];
            var callIds = current.Content
                .OfType<ToolCallContentBlock>()
                .Select(static block => block.CallId)
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
            {
                units.Add(new AtomicUnit([current.Message]));
                continue;
            }

            var groupedMessages = new List<RuntimeProviderMessage> { current.Message };
            while (index + 1 < messages.Length)
            {
                var candidate = messages[index + 1];
                var resultIds = candidate.Content
                    .OfType<ToolResultContentBlock>()
                    .Select(static block => block.CallId)
                    .ToArray();
                if (candidate.Message.Role != RuntimeProviderRoles.Tool
                    || resultIds.Length == 0
                    || resultIds.Any(resultId => !callIds.Contains(resultId)))
                {
                    break;
                }

                index++;
                groupedMessages.Add(candidate.Message);
            }

            units.Add(new AtomicUnit(groupedMessages));
        }

        return units;
    }

    private static ProjectedMessage CreateProjectedMessage(ConversationRecordV1 record)
    {
        var content = record.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray)
            ?? throw new InvalidDataException("A conversation record has no content.");
        return new ProjectedMessage(
            new RuntimeProviderMessage(record.Role, content),
            content);
    }

    internal sealed record Result(
        RuntimeProviderMessage[] Messages,
        string Strategy,
        int DroppedMessageCount,
        int EstimatedTokens,
        string EstimateSource)
    {
        public bool IsAdjusted => DroppedMessageCount > 0;
    }

    private sealed record ProjectedMessage(
        RuntimeProviderMessage Message,
        ContentBlock[] Content);

    private sealed record AtomicUnit(
        IReadOnlyList<RuntimeProviderMessage> Messages);

    private readonly record struct TokenEstimate(int Tokens, string Source);
}
