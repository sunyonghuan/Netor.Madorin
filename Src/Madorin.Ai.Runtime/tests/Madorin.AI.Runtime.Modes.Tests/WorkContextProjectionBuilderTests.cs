using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkContextProjectionBuilderTests
{
    private const int TokenLimit = 700;
    private static readonly string[] RequiredRetainedItems =
    [
        "plan:1",
        "step:step-099",
        "dependency:step-098",
        "tool-round:call-1"
    ];

    [TestMethod]
    public async Task BuildAsync_LongPlanAndLargeResults_PreservesRequiredUnitsWithinBudget()
    {
        var drafts = Enumerable.Range(0, 100)
            .Select(index => new WorkPlanStepDraft(
                $"step-{index:D3}",
                new string((char)('a' + index % 26), 600),
                "worker",
                index == 0 ? null : [$"step-{index - 1:D3}"]))
            .ToArray();
        var plan = new WorkPlanDraft("1", new string('g', 5_000), drafts);
        var states = drafts
            .Select((draft, index) => new WorkStepSnapshot(
                draft.StepId,
                "1",
                "session-1",
                "run-1",
                draft.TargetAgentId,
                draft.Goal,
                index == drafts.Length - 1
                    ? WorkStepLifecycleStatus.Pending
                    : WorkStepLifecycleStatus.Completed,
                $"hash-{index}",
                DependsOn: draft.DependsOn,
                ResultJson: index == 98 ? new string('r', 20_000) : $"result-{index}",
                StepMessageId: $"message-{index}"))
            .ToArray();
        using var arguments = JsonDocument.Parse("{\"path\":\"report.txt\"}");
        RuntimeProviderMessage[] priorMessages =
        [
            new(
                RuntimeProviderRoles.Assistant,
                [new ToolCallContentBlock(
                    "call-1",
                    "host.files.read",
                    "read",
                    arguments.RootElement.Clone())]),
            new(
                RuntimeProviderRoles.Tool,
                [new ToolResultContentBlock(
                    "call-1",
                    "host.files.read",
                    Success: true,
                    [new TextContentBlock(new string('t', 20_000))])])
        ];

        var result = await WorkContextProjectionBuilder.BuildAsync(
            plan,
            drafts[^1],
            states[^1],
            states,
            WorkContextPolicy.Default with { MaxTokens = TokenLimit },
            TokenLimit,
            EstimateTokensAsync,
            priorMessages,
            TestContext.CancellationToken);

        Assert.IsTrue(result.IsAdjusted);
        Assert.IsLessThanOrEqualTo(TokenLimit, result.EstimatedTokens);
        Assert.IsGreaterThanOrEqualTo(3, result.SummarizedUnitCount);
        foreach (var retainedItem in RequiredRetainedItems)
        {
            CollectionAssert.Contains(result.RetainedItems, retainedItem);
        }

        var texts = result.Messages
            .SelectMany(static message => message.Content)
            .OfType<TextContentBlock>()
            .Select(static block => block.Text)
            .ToArray();
        Assert.IsTrue(texts.Any(static text => text.StartsWith(
            "[WORK PLAN REFERENCE]",
            StringComparison.Ordinal)));
        Assert.IsTrue(texts.Any(static text => text.StartsWith(
            "[CURRENT STEP REFERENCE]",
            StringComparison.Ordinal)));
        Assert.IsTrue(texts.Any(static text => text.Contains(
            "step-output.summary",
            StringComparison.Ordinal)));

        var toolCallIndex = Array.FindIndex(
            result.Messages,
            static message => message.Content.OfType<ToolCallContentBlock>()
                .Any(call => call.CallId == "call-1"));
        var toolResultIndex = Array.FindIndex(
            result.Messages,
            static message => message.Content.OfType<ToolResultContentBlock>()
                .Any(toolResult => toolResult.CallId == "call-1"));
        Assert.IsGreaterThanOrEqualTo(0, toolCallIndex);
        Assert.AreEqual(toolCallIndex + 1, toolResultIndex);
        var compactedResult = result.Messages[toolResultIndex].Content
            .OfType<ToolResultContentBlock>()
            .Single();
        StringAssert.Contains(
            ((TextContentBlock)compactedResult.Content[0]).Text,
            "tool-result.summary");
    }

    public TestContext TestContext { get; set; }

    private static ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderMessage[] messages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProviderTokenEstimator.Estimate(
            new RuntimeProviderRequest(
                "invocation-1",
                "worker",
                "fake",
                "test-model",
                messages,
                CancellationToken: ct)));
    }
}
