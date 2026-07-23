using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Meeting;

/// <summary>
/// Provides meeting-aware context projection with round grouping, role filtering,
/// current-round preservation, and tool-call/result atomicity.
/// </summary>
internal static class MeetingContextProjectionService
{
    /// <summary>
    /// Builds a projected context for a meeting participant/role.
    /// The current round is always fully preserved. Historical rounds are selected by strategy.
    /// Tool-call and tool-result messages are never split across projection boundaries.
    /// </summary>
    internal static async Task<MeetingProjectionResult> BuildAsync(
        MeetingProjectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.History);
        ArgumentNullException.ThrowIfNull(request.InvocationToRound);
        ArgumentNullException.ThrowIfNull(request.Estimator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TokenLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.CurrentRound);
        if (request.TailMessageCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TailMessageCount must be > 0 when provided.");
        }

        if (request.SlidingRoundCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "SlidingRoundCount must be > 0 when provided.");
        }

        var units = BuildRoundAtomicUnits(request.History, request.InvocationToRound, request.CurrentRound);

        if (units.Count == 0)
        {
            return new MeetingProjectionResult(
                [],
                "Full",
                DroppedRoundCount: 0,
                DroppedMessageCount: 0,
                EstimatedTokens: 0,
                EstimateSource: "runtime.empty",
                TotalRoundCount: 0,
                IncludedRoundCount: 0,
                AdjustmentReason: null);
        }

        var totalRounds = units.Select(static u => u.RoundIndex).Distinct().Count();
        var originalMessageCount = units.Sum(static u => u.Messages.Count);

        var filteredUnits = ApplyRoleFilter(units, request.RoleFilter);
        filteredUnits = ApplyMeetingRoleFilter(filteredUnits, request.TargetRole, request.InvocationToRole);

        var currentUnits = filteredUnits.Where(static u => u.IsCurrentRound).ToList();
        var historicalUnits = filteredUnits.Where(static u => !u.IsCurrentRound).ToList();

        var allMessages = filteredUnits.SelectMany(static u => u.Messages).ToArray();
        var allEstimate = await EstimateAsync(allMessages, request.Estimator, ct).ConfigureAwait(false);

        var roleFilterDroppedCount = originalMessageCount - allMessages.Length;

        if (request.Strategy == MeetingContextStrategy.Full && allEstimate.Tokens <= request.TokenLimit)
        {
            return new MeetingProjectionResult(
                allMessages,
                "Full",
                DroppedRoundCount: 0,
                DroppedMessageCount: roleFilterDroppedCount,
                allEstimate.Tokens,
                allEstimate.Source,
                TotalRoundCount: totalRounds,
                IncludedRoundCount: totalRounds,
                AdjustmentReason: roleFilterDroppedCount > 0 ? "role-filtered" : null);
        }

        var (selectedHistorical, strategy, adjustmentReason) = await ApplyStrategyAsync(
            historicalUnits,
            currentUnits,
            request,
            allEstimate,
            ct).ConfigureAwait(false);

        var selectedMessages = selectedHistorical
            .SelectMany(static u => u.Messages)
            .Concat(currentUnits.SelectMany(static u => u.Messages))
            .ToArray();

        var finalEstimate = selectedMessages.Length > 0
            ? await EstimateAsync(selectedMessages, request.Estimator, ct).ConfigureAwait(false)
            : new TokenEstimate(0, "runtime.empty");

        var droppedMessages = originalMessageCount - selectedMessages.Length;
        var includedRounds = selectedHistorical.Select(static u => u.RoundIndex).Distinct().Count()
            + currentUnits.Select(static u => u.RoundIndex).Distinct().Count();

        return new MeetingProjectionResult(
            selectedMessages,
            strategy,
            DroppedRoundCount: Math.Max(0, totalRounds - includedRounds),
            DroppedMessageCount: droppedMessages,
            finalEstimate.Tokens,
            finalEstimate.Source,
            TotalRoundCount: totalRounds,
            IncludedRoundCount: includedRounds,
            AdjustmentReason: adjustmentReason);
    }

    private static async Task<(List<MeetingAtomicUnit> Selected, string Strategy, string? Reason)> ApplyStrategyAsync(
        List<MeetingAtomicUnit> historicalUnits,
        List<MeetingAtomicUnit> currentUnits,
        MeetingProjectionRequest request,
        TokenEstimate allEstimate,
        CancellationToken ct)
    {
        switch (request.Strategy)
        {
            case MeetingContextStrategy.Full:
            {
                var reason = allEstimate.Tokens > request.TokenLimit
                    ? "full-exceeds-token-limit"
                    : null;
                return (historicalUnits, "Full", reason);
            }

            case MeetingContextStrategy.TailWindow:
            {
                var tailCount = request.TailMessageCount ?? 5;
                var selected = SelectTailRounds(historicalUnits, tailCount);
                var reason = selected.Count < historicalUnits.Count
                    ? "tail-window-trimmed"
                    : null;
                return (selected, "TailWindow", reason);
            }

            case MeetingContextStrategy.SlidingWindow:
            {
                var windowSize = request.SlidingRoundCount ?? 3;
                var selected = SelectSlidingRounds(historicalUnits, windowSize);
                var reason = selected.Count < historicalUnits.Count
                    ? "sliding-window-trimmed"
                    : null;
                return (selected, "SlidingWindow", reason);
            }

            case MeetingContextStrategy.SummaryCompressed:
            {
                var (selected, reason) = SelectSummaryCompressed(historicalUnits);
                return (selected, "SummaryCompressed", reason);
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"Unknown meeting context strategy: {request.Strategy}");
        }
    }

    private static List<MeetingAtomicUnit> SelectTailRounds(
        List<MeetingAtomicUnit> historicalUnits,
        int tailCount)
    {
        var roundIndices = historicalUnits
            .Select(static u => u.RoundIndex)
            .Distinct()
            .OrderBy(static r => r)
            .ToList();

        if (roundIndices.Count <= tailCount)
        {
            return historicalUnits;
        }

        var selectedRounds = new HashSet<int>(
            roundIndices.Skip(roundIndices.Count - tailCount));
        return historicalUnits
            .Where(u => selectedRounds.Contains(u.RoundIndex))
            .ToList();
    }

    private static List<MeetingAtomicUnit> SelectSlidingRounds(
        List<MeetingAtomicUnit> historicalUnits,
        int windowSize)
    {
        var roundIndices = historicalUnits
            .Select(static u => u.RoundIndex)
            .Distinct()
            .OrderBy(static r => r)
            .ToList();

        if (roundIndices.Count <= windowSize)
        {
            return historicalUnits;
        }

        var selectedRounds = new HashSet<int>(
            roundIndices.Skip(roundIndices.Count - windowSize));
        return historicalUnits
            .Where(u => selectedRounds.Contains(u.RoundIndex))
            .ToList();
    }

    private static (List<MeetingAtomicUnit> Selected, string? Reason) SelectSummaryCompressed(
        List<MeetingAtomicUnit> historicalUnits)
    {
        var summaryUnits = historicalUnits.Where(static u => u.HasSummary).ToList();
        var regularUnits = historicalUnits.Where(static u => !u.HasSummary).ToList();

        if (summaryUnits.Count == 0)
        {
            return (historicalUnits, "summary-compressed-no-summaries");
        }

        var latestSummary = summaryUnits
            .Select(static unit => new
            {
                Unit = unit,
                SummarizesThroughSeq = unit.SourceRecords
                    .Where(static record => record.SummaryMetadata is not null)
                    .Select(static record =>
                    {
                        var metadata = record.SummaryMetadata!.Value;
                        return metadata.TryGetProperty("summarizesThroughSeq", out var sequence)
                            ? sequence.GetInt64()
                            : 0L;
                    })
                    .DefaultIfEmpty(0L)
                    .Max(),
                MessageSequence = unit.SourceRecords.Max(static record => record.Sequence)
            })
            .OrderByDescending(static candidate => candidate.SummarizesThroughSeq)
            .ThenByDescending(static candidate => candidate.MessageSequence)
            .First();
        var maxSummarizedSeq = latestSummary.SummarizesThroughSeq;

        var unsummarizedUnits = regularUnits
            .Where(u => u.SourceRecords.Any(r => r.Sequence > maxSummarizedSeq))
            .ToList();

        var selected = new List<MeetingAtomicUnit>(1 + unsummarizedUnits.Count)
        {
            latestSummary.Unit
        };
        selected.AddRange(unsummarizedUnits);
        var reason = selected.Count < historicalUnits.Count
            ? "summary-compressed"
            : null;
        return (selected, reason);
    }

    private static List<MeetingAtomicUnit> ApplyRoleFilter(
        List<MeetingAtomicUnit> units,
        MeetingRoleFilter? filter)
    {
        if (filter is null || filter == MeetingRoleFilter.None)
        {
            return units;
        }

        return units.Where(unit =>
        {
            foreach (var message in unit.Messages)
            {
                if (IsRoleAllowed(message.Role, filter))
                {
                    return true;
                }
            }

            return false;
        }).ToList();
    }

    private static bool IsRoleAllowed(string role, MeetingRoleFilter filter)
    {
        return role switch
        {
            RuntimeProviderRoles.Assistant => !filter.ExcludeAssistant,
            RuntimeProviderRoles.Tool => !filter.ExcludeTool,
            RuntimeProviderRoles.User => !filter.ExcludeUser,
            RuntimeProviderRoles.System => !filter.ExcludeSystem,
            _ => true
        };
    }

    private static List<MeetingAtomicUnit> ApplyMeetingRoleFilter(
        List<MeetingAtomicUnit> units,
        MeetingInvocationRole? targetRole,
        IReadOnlyDictionary<string, MeetingInvocationRole>? invocationToRole)
    {
        if (targetRole is null || invocationToRole is null)
        {
            return units;
        }

        return units.Where(unit =>
        {
            var mappedRoles = unit.SourceRecords
                .Select(r => invocationToRole.TryGetValue(r.InvocationId, out var role) ? (MeetingInvocationRole?)role : null)
                .ToList();

            if (mappedRoles.All(static r => r is null))
            {
                return true;
            }

            var representativeRole = mappedRoles.FirstOrDefault(static r => r is not null);
            return representativeRole is not null && IsVisibleAs(representativeRole.Value, targetRole.Value);
        }).ToList();
    }

    private static bool IsVisibleAs(MeetingInvocationRole sourceRole, MeetingInvocationRole targetRole)
    {
        return targetRole switch
        {
            MeetingInvocationRole.Participant => sourceRole != MeetingInvocationRole.Selector,
            MeetingInvocationRole.Selector => sourceRole is MeetingInvocationRole.Participant or MeetingInvocationRole.Summarizer,
            MeetingInvocationRole.Host => sourceRole is MeetingInvocationRole.Participant or MeetingInvocationRole.Host,
            MeetingInvocationRole.Summarizer => sourceRole is MeetingInvocationRole.Participant or MeetingInvocationRole.Host,
            _ => true
        };
    }

    private static List<MeetingAtomicUnit> BuildRoundAtomicUnits(
        IReadOnlyList<ConversationRecordV1> history,
        IReadOnlyDictionary<string, int> invocationToRound,
        int currentRound)
    {
        var projected = history.Select(record =>
        {
            var content = record.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray)
                ?? throw new InvalidDataException("A conversation record has no content.");
            var roundIndex = invocationToRound.TryGetValue(record.InvocationId, out var round)
                ? round
                : 0;
            var hasSummary = record.SummaryMetadata is not null;
            return new ProjectedRecord(record, content, roundIndex, hasSummary);
        }).ToArray();

        var units = new List<MeetingAtomicUnit>(projected.Length);
        for (var index = 0; index < projected.Length; index++)
        {
            var current = projected[index];
            var callIds = current.Content
                .OfType<ToolCallContentBlock>()
                .Select(static block => block.CallId)
                .ToHashSet(StringComparer.Ordinal);

            if (callIds.Count == 0)
            {
                units.Add(new MeetingAtomicUnit(
                    [new RuntimeProviderMessage(current.Record.Role, current.Content)],
                    current.RoundIndex,
                    current.HasSummary,
                    [current.Record])
                {
                    IsCurrentRound = current.RoundIndex == currentRound
                });
                continue;
            }

            var groupedMessages = new List<RuntimeProviderMessage>
            {
                new(current.Record.Role, current.Content)
            };
            var groupedRecords = new List<ConversationRecordV1> { current.Record };
            var hasSummary = current.HasSummary;

            while (index + 1 < projected.Length)
            {
                var candidate = projected[index + 1];
                var resultIds = candidate.Content
                    .OfType<ToolResultContentBlock>()
                    .Select(static block => block.CallId)
                    .ToArray();

                if (candidate.Record.Role != RuntimeProviderRoles.Tool
                    || resultIds.Length == 0
                    || resultIds.Any(resultId => !callIds.Contains(resultId)))
                {
                    break;
                }

                index++;
                groupedMessages.Add(new RuntimeProviderMessage(candidate.Record.Role, candidate.Content));
                groupedRecords.Add(candidate.Record);
                hasSummary |= candidate.HasSummary;
            }

            units.Add(new MeetingAtomicUnit(
                groupedMessages,
                current.RoundIndex,
                hasSummary,
                groupedRecords)
            {
                IsCurrentRound = current.RoundIndex == currentRound
            });
        }

        return units;
    }

    private static async ValueTask<TokenEstimate> EstimateAsync(
        RuntimeProviderMessage[] messages,
        Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> estimator,
        CancellationToken ct)
    {
        if (messages.Length == 0)
        {
            return new TokenEstimate(0, "runtime.empty");
        }

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

    private readonly record struct TokenEstimate(int Tokens, string Source);

    private sealed record ProjectedRecord(
        ConversationRecordV1 Record,
        ContentBlock[] Content,
        int RoundIndex,
        bool HasSummary);
}

/// <summary>Input request for <see cref="MeetingContextProjectionService"/>.</summary>
internal sealed record MeetingProjectionRequest(
    IReadOnlyList<ConversationRecordV1> History,
    IReadOnlyDictionary<string, int> InvocationToRound,
    int CurrentRound,
    int TokenLimit,
    MeetingContextStrategy Strategy = MeetingContextStrategy.Full,
    int? TailMessageCount = null,
    int? SlidingRoundCount = null,
    MeetingRoleFilter? RoleFilter = null,
    IReadOnlyDictionary<string, MeetingInvocationRole>? InvocationToRole = null,
    MeetingInvocationRole? TargetRole = null)
{
    internal Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> Estimator { get; init; } = FallbackEstimator;

    private static ValueTask<ProviderTokenEstimate> FallbackEstimator(
        RuntimeProviderMessage[] messages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new RuntimeProviderRequest(
            "projection",
            "projection",
            "fake",
            "fake-model",
            messages,
            CancellationToken: ct);
        return ValueTask.FromResult(ProviderTokenEstimator.Estimate(request));
    }
}

/// <summary>Result of a meeting context projection.</summary>
internal sealed record MeetingProjectionResult(
    RuntimeProviderMessage[] Messages,
    string Strategy,
    int DroppedRoundCount,
    int DroppedMessageCount,
    int EstimatedTokens,
    string EstimateSource,
    int TotalRoundCount,
    int IncludedRoundCount,
    string? AdjustmentReason)
{
    /// <summary>True when the projection dropped messages or rounds from the full history.</summary>
    public bool IsAdjusted => DroppedMessageCount > 0 || DroppedRoundCount > 0;
}

/// <summary>
/// An atomic unit of meeting messages that must not be split during projection.
/// Tool-call messages and their corresponding tool-results are grouped together.
/// </summary>
internal sealed record MeetingAtomicUnit(
    IReadOnlyList<RuntimeProviderMessage> Messages,
    int RoundIndex,
    bool HasSummary,
    IReadOnlyList<ConversationRecordV1> SourceRecords)
{
    /// <summary>True when this unit belongs to the current (active) round.</summary>
    public bool IsCurrentRound { get; init; }
}

/// <summary>
/// Controls which message roles are excluded from a meeting projection.
/// Filtering only affects the projection output, never the original records.
/// </summary>
internal sealed record MeetingRoleFilter(
    bool ExcludeAssistant = false,
    bool ExcludeTool = false,
    bool ExcludeUser = false,
    bool ExcludeSystem = false)
{
    /// <summary>A filter that excludes no roles.</summary>
    public static readonly MeetingRoleFilter None = new();

    /// <summary>Creates a filter that excludes the specified roles.</summary>
    public static MeetingRoleFilter Exclude(params string[] roles)
    {
        var excludeAssistant = false;
        var excludeTool = false;
        var excludeUser = false;
        var excludeSystem = false;

        foreach (var role in roles)
        {
            switch (role)
            {
                case RuntimeProviderRoles.Assistant:
                    excludeAssistant = true;
                    break;
                case RuntimeProviderRoles.Tool:
                    excludeTool = true;
                    break;
                case RuntimeProviderRoles.User:
                    excludeUser = true;
                    break;
                case RuntimeProviderRoles.System:
                    excludeSystem = true;
                    break;
            }
        }

        return new MeetingRoleFilter(excludeAssistant, excludeTool, excludeUser, excludeSystem);
    }
}
