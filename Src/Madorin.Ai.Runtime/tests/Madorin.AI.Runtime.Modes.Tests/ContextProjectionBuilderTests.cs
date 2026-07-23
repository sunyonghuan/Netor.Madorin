using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class ContextProjectionBuilderTests
{
    private static readonly string[] TailWindowRoles = ["assistant", "tool", "assistant"];

    [TestMethod]
    public async Task BuildAsync_WhenHistoryFits_UsesFullProjection()
    {
        ConversationRecordV1[] history =
        [
            CreateRecord(1, "user", new TextContentBlock("question")),
            CreateRecord(2, "assistant", new TextContentBlock("answer"))
        ];

        var result = await ContextProjectionBuilder.BuildAsync(
            history,
            tokenLimit: 1000,
            EstimateTokensAsync);

        Assert.AreEqual("Full", result.Strategy);
        Assert.AreEqual(0, result.DroppedMessageCount);
        Assert.HasCount(2, result.Messages);
        Assert.AreEqual("provider.estimated", result.EstimateSource);
    }

    [TestMethod]
    public async Task BuildAsync_WhenTailWindowStartsAtToolRound_KeepsCallAndResultTogether()
    {
        using var arguments = JsonDocument.Parse("{}");
        ConversationRecordV1[] history =
        [
            CreateRecord(1, "user", new TextContentBlock(new string('x', 10_000))),
            CreateRecord(
                2,
                "assistant",
                new ToolCallContentBlock("call-1", "builtin.test", "test", arguments.RootElement.Clone())),
            CreateRecord(
                3,
                "tool",
                new ToolResultContentBlock(
                    "call-1",
                    "builtin.test",
                    Success: true,
                    [new TextContentBlock("result")])),
            CreateRecord(4, "assistant", new TextContentBlock("final"))
        ];

        var result = await ContextProjectionBuilder.BuildAsync(
            history,
            tokenLimit: 200,
            EstimateTokensAsync);

        Assert.AreEqual("TailWindow", result.Strategy);
        Assert.AreEqual(1, result.DroppedMessageCount);
        CollectionAssert.AreEqual(
            TailWindowRoles,
            result.Messages.Select(static message => message.Role).ToArray());
        Assert.IsInstanceOfType<ToolCallContentBlock>(result.Messages[0].Content[0]);
        Assert.IsInstanceOfType<ToolResultContentBlock>(result.Messages[1].Content[0]);
    }

    [TestMethod]
    public async Task BuildAsync_WhenToolRoundExceedsLimit_IncludesWholeAtomicUnit()
    {
        using var arguments = JsonDocument.Parse("{}");
        ConversationRecordV1[] history =
        [
            CreateRecord(
                1,
                "assistant",
                new ToolCallContentBlock("call-1", "builtin.test", "test", arguments.RootElement.Clone())),
            CreateRecord(
                2,
                "tool",
                new ToolResultContentBlock(
                    "call-1",
                    "builtin.test",
                    Success: true,
                    [new TextContentBlock(new string('r', 1000))]))
        ];

        var result = await ContextProjectionBuilder.BuildAsync(
            history,
            tokenLimit: 1,
            EstimateTokensAsync);

        Assert.AreEqual("TailWindow", result.Strategy);
        Assert.AreEqual(0, result.DroppedMessageCount);
        Assert.HasCount(2, result.Messages);
        Assert.IsFalse(result.IsAdjusted);
    }

    private static ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderMessage[] messages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new RuntimeProviderRequest(
            "invocation-1",
            "agent-1",
            "fake",
            "fake-model",
            messages,
            CancellationToken: ct);
        return ValueTask.FromResult(ProviderTokenEstimator.Estimate(request));
    }

    private static ConversationRecordV1 CreateRecord(
        long sequence,
        string role,
        params ContentBlock[] content) =>
        new(
            $"message-{sequence}",
            sequence,
            "invocation-1",
            "agent-1",
            role,
            JsonSerializer.SerializeToElement(
                content,
                RuntimeJsonContext.Default.ContentBlockArray),
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));
}
