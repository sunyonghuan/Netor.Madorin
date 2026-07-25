using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Work;

internal static class WorkContextProjectionBuilder
{
    private const int LargeResultBytes = 2 * 1024;
    private const int SummaryPrefixCharacters = 384;

    internal static async Task<Result> BuildAsync(
        WorkPlanDraft plan,
        WorkPlanStepDraft currentStep,
        WorkStepSnapshot currentState,
        IReadOnlyList<WorkStepSnapshot> persistedSteps,
        WorkContextPolicy policy,
        int tokenLimit,
        Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> estimator,
        IReadOnlyList<RuntimeProviderMessage>? priorMessages = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentStep);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(persistedSteps);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenLimit);

        var units = BuildUnits(
            plan,
            currentStep,
            currentState,
            persistedSteps,
            policy,
            priorMessages ?? []);
        var fullMessages = Flatten(units);
        var fullEstimate = await EstimateAsync(fullMessages, estimator, ct).ConfigureAwait(false);
        var tailLimit = policy.TailMessageCount is > 0
            ? policy.TailMessageCount.Value
            : int.MaxValue;
        if (fullEstimate.Tokens <= tokenLimit && fullMessages.Length <= tailLimit)
        {
            return CreateResult(
                units,
                "Full",
                droppedMessageCount: 0,
                summarizedUnitCount: units.Count(static unit => unit.IsSummary),
                fullEstimate,
                tokenLimit,
                isAdjusted: units.Any(static unit => unit.IsSummary));
        }

        var selected = units.ToList();
        var droppedMessageCount = 0;
        while (true)
        {
            var messages = Flatten(selected);
            var estimate = await EstimateAsync(messages, estimator, ct).ConfigureAwait(false);
            if (estimate.Tokens <= tokenLimit && messages.Length <= tailLimit)
            {
                return CreateResult(
                    selected,
                    "Budgeted",
                    droppedMessageCount,
                    selected.Count(static unit => unit.IsSummary),
                    estimate,
                    tokenLimit,
                    isAdjusted: true);
            }

            var optionalIndex = selected.FindIndex(static unit => !unit.IsRequired);
            if (optionalIndex >= 0)
            {
                droppedMessageCount += selected[optionalIndex].Messages.Count;
                selected.RemoveAt(optionalIndex);
                continue;
            }

            var compactableIndex = selected.FindIndex(static unit => unit.CompactMessages is not null);
            if (compactableIndex >= 0)
            {
                var unit = selected[compactableIndex];
                selected[compactableIndex] = unit with
                {
                    Messages = unit.CompactMessages!,
                    CompactMessages = null,
                    IsSummary = true
                };
                continue;
            }

            return CreateResult(
                selected,
                "RequiredUnitsExceedBudget",
                droppedMessageCount,
                selected.Count(static unit => unit.IsSummary),
                estimate,
                tokenLimit,
                isAdjusted: true);
        }
    }

    private static List<AtomicUnit> BuildUnits(
        WorkPlanDraft plan,
        WorkPlanStepDraft currentStep,
        WorkStepSnapshot currentState,
        IReadOnlyList<WorkStepSnapshot> persistedSteps,
        WorkContextPolicy policy,
        IReadOnlyList<RuntimeProviderMessage> priorMessages)
    {
        var units = new List<AtomicUnit>();
        var dependencyIds = (currentStep.DependsOn ?? [])
            .ToHashSet(StringComparer.Ordinal);

        if (policy.PreservePlan)
        {
            var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
            units.Add(new AtomicUnit(
                $"plan:{plan.PlanVersion}",
                [CreateTextMessage("[WORK PLAN]\n" + planJson)],
                IsRequired: true,
                CompactMessages:
                [CreateTextMessage(BuildCompactPlan(plan, currentStep, dependencyIds))]));
        }

        if (policy.PreserveCurrentStep)
        {
            var stepJson = JsonSerializer.Serialize(
                currentStep,
                RuntimeJsonContext.Default.WorkPlanStepDraft);
            units.Add(new AtomicUnit(
                $"step:{currentStep.StepId}",
                [CreateTextMessage("[CURRENT STEP]\n" + stepJson)],
                IsRequired: true,
                CompactMessages:
                [CreateTextMessage(BuildCompactStep(currentStep, currentState))]));
        }

        foreach (var dependencyId in dependencyIds.Order(StringComparer.Ordinal))
        {
            var dependency = persistedSteps.FirstOrDefault(step =>
                string.Equals(step.StepId, dependencyId, StringComparison.Ordinal));
            if (dependency is null)
            {
                continue;
            }

            var full = BuildDependencyOutput(dependency, summarize: false);
            var compact = BuildDependencyOutput(dependency, summarize: true);
            var alreadySummarized = Encoding.UTF8.GetByteCount(dependency.ResultJson ?? string.Empty)
                > LargeResultBytes;
            units.Add(new AtomicUnit(
                $"dependency:{dependency.StepId}",
                [CreateTextMessage(alreadySummarized ? compact : full)],
                IsRequired: true,
                CompactMessages: alreadySummarized ? null : [CreateTextMessage(compact)],
                IsSummary: alreadySummarized));
        }

        foreach (var completed in persistedSteps
            .Where(step => step.Status is WorkStepLifecycleStatus.Completed
                && !dependencyIds.Contains(step.StepId)
                && !string.Equals(step.StepId, currentStep.StepId, StringComparison.Ordinal))
            .OrderByDescending(static step => step.CompletedAt)
            .ThenBy(static step => step.StepId, StringComparer.Ordinal))
        {
            units.Add(new AtomicUnit(
                $"completed:{completed.StepId}",
                [CreateTextMessage(BuildDependencyOutput(completed, summarize: true))],
                IsRequired: false,
                CompactMessages: null,
                IsSummary: true));
        }

        if (policy.PreserveToolResults && priorMessages.Count > 0)
        {
            units.AddRange(BuildToolRoundUnits(priorMessages));
        }

        return units;
    }

    private static IEnumerable<AtomicUnit> BuildToolRoundUnits(
        IReadOnlyList<RuntimeProviderMessage> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var current = messages[index];
            var callIds = current.Content
                .OfType<ToolCallContentBlock>()
                .Select(static call => call.CallId)
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
            {
                yield return new AtomicUnit(
                    $"history:{index}",
                    [current],
                    IsRequired: false,
                    CompactMessages: null);
                continue;
            }

            var grouped = new List<RuntimeProviderMessage> { current };
            var retainedCallId = callIds.Order(StringComparer.Ordinal).First();
            while (index + 1 < messages.Count)
            {
                var candidate = messages[index + 1];
                var resultIds = candidate.Content
                    .OfType<ToolResultContentBlock>()
                    .Select(static result => result.CallId)
                    .ToArray();
                if (!string.Equals(candidate.Role, RuntimeProviderRoles.Tool, StringComparison.Ordinal)
                    || resultIds.Length == 0
                    || resultIds.Any(resultId => !callIds.Contains(resultId)))
                {
                    break;
                }

                index++;
                grouped.Add(candidate);
            }

            yield return new AtomicUnit(
                $"tool-round:{retainedCallId}",
                grouped,
                IsRequired: true,
                CompactMessages: CompactToolRound(grouped),
                IsSummary: false);
        }
    }

    private static RuntimeProviderMessage[]? CompactToolRound(
        List<RuntimeProviderMessage> messages)
    {
        var changed = false;
        var compacted = new RuntimeProviderMessage[messages.Count];
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var blocks = new ContentBlock[message.Content.Length];
            for (var blockIndex = 0; blockIndex < message.Content.Length; blockIndex++)
            {
                var block = message.Content[blockIndex];
                if (block is ToolResultContentBlock result)
                {
                    var raw = JsonSerializer.Serialize(
                        result.Content,
                        RuntimeJsonContext.Default.ContentBlockArray);
                    if (Encoding.UTF8.GetByteCount(raw) > LargeResultBytes)
                    {
                        changed = true;
                        blocks[blockIndex] = result with
                        {
                            Content = [new TextContentBlock(CreateSummary(raw, "tool-result"))]
                        };
                        continue;
                    }
                }

                blocks[blockIndex] = block;
            }

            compacted[index] = message with { Content = blocks };
        }

        return changed ? compacted : null;
    }

    private static string BuildCompactPlan(
        WorkPlanDraft plan,
        WorkPlanStepDraft currentStep,
        HashSet<string> dependencyIds)
    {
        var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
        var relevantStepIds = plan.Steps
            .Where(step => string.Equals(step.StepId, currentStep.StepId, StringComparison.Ordinal)
                || dependencyIds.Contains(step.StepId))
            .Select(static step => step.StepId)
            .Order(StringComparer.Ordinal);
        return $"[WORK PLAN REFERENCE]\nplanVersion={plan.PlanVersion}\n"
            + $"stepCount={plan.Steps.Length}\nsha256={ComputeHash(planJson)}\n"
            + $"goal={BoundedPrefix(plan.Goal)}\nrelevantSteps={string.Join(',', relevantStepIds)}";
    }

    private static string BuildCompactStep(
        WorkPlanStepDraft step,
        WorkStepSnapshot state)
    {
        var stepJson = JsonSerializer.Serialize(step, RuntimeJsonContext.Default.WorkPlanStepDraft);
        return $"[CURRENT STEP REFERENCE]\nstepId={step.StepId}\n"
            + $"planVersion={state.PlanVersion}\nstepInputHash={state.StepInputHash}\n"
            + $"stepMessageId={state.StepMessageId}\nsha256={ComputeHash(stepJson)}\n"
            + $"goal={BoundedPrefix(step.Goal)}";
    }

    private static string BuildDependencyOutput(WorkStepSnapshot step, bool summarize)
    {
        var result = step.ResultJson ?? string.Empty;
        var body = summarize ? CreateSummary(result, "step-output") : result;
        return $"[DEPENDENCY OUTPUT]\nstepId={step.StepId}\nplanVersion={step.PlanVersion}\n"
            + $"status={step.Status}\nstepMessageId={step.StepMessageId}\n{body}";
    }

    private static string CreateSummary(string value, string kind)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        return $"{kind}.summary sha256={ComputeHash(value)} bytes={bytes}\n"
            + BoundedPrefix(value);
    }

    private static string BoundedPrefix(string value) =>
        value.Length <= SummaryPrefixCharacters
            ? value
            : value[..SummaryPrefixCharacters];

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static RuntimeProviderMessage CreateTextMessage(string text) =>
        new(RuntimeProviderRoles.User, [new TextContentBlock(text)]);

    private static RuntimeProviderMessage[] Flatten(IReadOnlyList<AtomicUnit> units) =>
        [.. units.SelectMany(static unit => unit.Messages)];

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

    private static Result CreateResult(
        IReadOnlyList<AtomicUnit> units,
        string strategy,
        int droppedMessageCount,
        int summarizedUnitCount,
        TokenEstimate estimate,
        int tokenLimit,
        bool isAdjusted) =>
        new(
            Flatten(units),
            strategy,
            [.. units.Select(static unit => unit.Name)],
            droppedMessageCount,
            summarizedUnitCount,
            estimate.Tokens,
            estimate.Source,
            tokenLimit,
            isAdjusted);

    internal sealed record Result(
        RuntimeProviderMessage[] Messages,
        string Strategy,
        string[] RetainedItems,
        int DroppedMessageCount,
        int SummarizedUnitCount,
        int EstimatedTokens,
        string EstimateSource,
        int TokenLimit,
        bool IsAdjusted);

    private sealed record AtomicUnit(
        string Name,
        IReadOnlyList<RuntimeProviderMessage> Messages,
        bool IsRequired,
        IReadOnlyList<RuntimeProviderMessage>? CompactMessages,
        bool IsSummary = false);

    private readonly record struct TokenEstimate(int Tokens, string Source);
}
