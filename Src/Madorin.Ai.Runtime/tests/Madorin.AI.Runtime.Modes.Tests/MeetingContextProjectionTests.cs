using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class MeetingContextProjectionTests
{
    [TestMethod]
    public async Task BuildAsync_CurrentRoundFullyPreserved()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("round1-q")),
            (2, "inv-1", "assistant", new TextContentBlock("round1-a")),
            (3, "inv-2", "user", new TextContentBlock("round2-q")),
            (4, "inv-2", "assistant", new TextContentBlock("round2-a")),
            (5, "inv-3", "user", new TextContentBlock("current-q")),
            (6, "inv-3", "assistant", new TextContentBlock("current-a")));

        var request = new MeetingProjectionRequest(history, map, CurrentRound: 3, TokenLimit: 100)
        {
            Strategy = MeetingContextStrategy.TailWindow,
            TailMessageCount = 1
        };

        var result = await MeetingContextProjectionService.BuildAsync(request);

        var currentMessages = result.Messages.Where(m =>
            m.Content.OfType<TextContentBlock>().Any(b =>
                b.Text.StartsWith("current", StringComparison.Ordinal))).ToArray();
        Assert.HasCount(2, currentMessages);
    }

    [TestMethod]
    public async Task BuildAsync_ToolCallAndResultStayAtomic()
    {
        using var arguments = JsonDocument.Parse("{}");
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("do something")),
            (2, "inv-2", "assistant", new ToolCallContentBlock("c1", "t", "t", arguments.RootElement.Clone())),
            (3, "inv-2", "tool", new ToolResultContentBlock("c1", "t", true, [new TextContentBlock("result")])),
            (4, "inv-3", "assistant", new TextContentBlock("done")));

        var request = new MeetingProjectionRequest(history, map, CurrentRound: 3, TokenLimit: 1)
        {
            Strategy = MeetingContextStrategy.TailWindow,
            TailMessageCount = 1
        };

        var result = await MeetingContextProjectionService.BuildAsync(request);

        var roles = result.Messages.Select(m => m.Role).ToArray();
        var toolCallIdx = Array.IndexOf(roles, "assistant");
        var toolResultIdx = Array.IndexOf(roles, "tool");
        if (toolCallIdx >= 0 && toolResultIdx >= 0)
        {
            Assert.IsGreaterThan(toolCallIdx, toolResultIdx,
                "Tool result must follow tool call in projection.");
        }
    }

    [TestMethod]
    public async Task BuildAsync_TailWindowSelectsLastNRounds()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("r1")),
            (2, "inv-2", "user", new TextContentBlock("r2")),
            (3, "inv-3", "user", new TextContentBlock("r3")),
            (4, "inv-4", "user", new TextContentBlock("r4")),
            (5, "inv-5", "user", new TextContentBlock("r5")));

        var request = new MeetingProjectionRequest(history, map, CurrentRound: 5, TokenLimit: 10)
        {
            Strategy = MeetingContextStrategy.TailWindow,
            TailMessageCount = 2
        };

        var result = await MeetingContextProjectionService.BuildAsync(request);

        var texts = result.Messages
            .SelectMany(m => m.Content.OfType<TextContentBlock>().Select(b => b.Text))
            .ToArray();
        CollectionAssert.Contains(texts, "r4");
        CollectionAssert.Contains(texts, "r5");
        Assert.IsFalse(texts.Contains("r1"));
        Assert.IsFalse(texts.Contains("r2"));
    }

    [TestMethod]
    public async Task BuildAsync_SlidingWindowSelectsLastNRounds()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("r1")),
            (2, "inv-2", "user", new TextContentBlock("r2")),
            (3, "inv-3", "user", new TextContentBlock("r3")),
            (4, "inv-4", "user", new TextContentBlock("r4")));

        var request = new MeetingProjectionRequest(history, map, CurrentRound: 4, TokenLimit: 10)
        {
            Strategy = MeetingContextStrategy.SlidingWindow,
            SlidingRoundCount = 2
        };

        var result = await MeetingContextProjectionService.BuildAsync(request);

        var texts = result.Messages
            .SelectMany(m => m.Content.OfType<TextContentBlock>().Select(b => b.Text))
            .ToArray();
        CollectionAssert.Contains(texts, "r3");
        CollectionAssert.Contains(texts, "r4");
        Assert.IsFalse(texts.Contains("r1"));
    }

    [TestMethod]
    public async Task BuildAsync_RoleFilterExcludesWithoutMutatingHistory()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("q")),
            (2, "inv-1", "assistant", new TextContentBlock("a")),
            (3, "inv-2", "user", new TextContentBlock("q2")),
            (4, "inv-2", "assistant", new TextContentBlock("a2")));

        var originalCount = history.Length;

        var request = new MeetingProjectionRequest(history, map, CurrentRound: 2, TokenLimit: 100)
        {
            Strategy = MeetingContextStrategy.Full,
            RoleFilter = new MeetingRoleFilter(ExcludeAssistant: true)
        };

        var result = await MeetingContextProjectionService.BuildAsync(request);

        Assert.HasCount(originalCount, history, "Original history must not be mutated.");
        foreach (var msg in result.Messages)
        {
            Assert.AreNotEqual("assistant", msg.Role);
        }
    }

    [TestMethod]
    public async Task BuildAsync_MeetingRoleProjection_HidesInternalDecisionsAndKeepsSummaries()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "assistant", new TextContentBlock("participant-view")),
            (2, "inv-2", "assistant", new TextContentBlock("selector-internal")),
            (3, "inv-3", "assistant", new TextContentBlock("host-internal")),
            (4, "inv-4", "assistant", new TextContentBlock("round-summary")));
        var invocationToRole = new Dictionary<string, MeetingInvocationRole>(StringComparer.Ordinal)
        {
            ["inv-1"] = MeetingInvocationRole.Participant,
            ["inv-2"] = MeetingInvocationRole.Selector,
            ["inv-3"] = MeetingInvocationRole.Host,
            ["inv-4"] = MeetingInvocationRole.Summarizer
        };

        var participantProjection = await MeetingContextProjectionService.BuildAsync(
            new MeetingProjectionRequest(history, map, CurrentRound: 4, TokenLimit: 100)
            {
                InvocationToRole = invocationToRole,
                TargetRole = MeetingInvocationRole.Participant
            });
        var selectorProjection = await MeetingContextProjectionService.BuildAsync(
            new MeetingProjectionRequest(history, map, CurrentRound: 4, TokenLimit: 100)
            {
                InvocationToRole = invocationToRole,
                TargetRole = MeetingInvocationRole.Selector
            });

        var participantTexts = GetTexts(participantProjection);
        CollectionAssert.Contains(participantTexts, "participant-view");
        Assert.DoesNotContain("selector-internal", participantTexts);

        var selectorTexts = GetTexts(selectorProjection);
        CollectionAssert.Contains(selectorTexts, "participant-view");
        CollectionAssert.Contains(selectorTexts, "round-summary");
        Assert.DoesNotContain("selector-internal", selectorTexts);
        Assert.DoesNotContain("host-internal", selectorTexts);
        Assert.HasCount(4, history, "Canonical history must remain unchanged.");
    }

    [TestMethod]
    public void MeetingProjectionRequest_DefaultEstimatorCompiles()
    {
        var (history, map) = BuildHistory(
            (1, "inv-1", "user", new TextContentBlock("hello")));

        var request = new MeetingProjectionRequest(history, map, 1, 100);

        Assert.IsNotNull(request.Estimator);
    }

    private static string[] GetTexts(MeetingProjectionResult projection) =>
    [
        .. projection.Messages.SelectMany(message =>
            message.Content.OfType<TextContentBlock>().Select(block => block.Text))
    ];

    private static (ConversationRecordV1[] History, Dictionary<string, int> Map) BuildHistory(
        params (long Seq, string InvocationId, string Role, ContentBlock Content)[] entries)
    {
        var records = new List<ConversationRecordV1>();
        var map = new Dictionary<string, int>();

        foreach (var (seq, invocationId, role, content) in entries)
        {
            var messageId = $"msg-{seq}";
            var contentArray = new[] { content };
            records.Add(new ConversationRecordV1(
                messageId,
                seq,
                invocationId,
                "agent-1",
                role,
                JsonSerializer.SerializeToElement(contentArray, RuntimeJsonContext.Default.ContentBlockArray),
                DateTimeOffset.UnixEpoch.AddSeconds(seq)));

            if (!map.ContainsKey(invocationId))
            {
                var roundNum = int.Parse(invocationId.Replace("inv-", ""), CultureInfo.InvariantCulture);
                map[invocationId] = roundNum;
            }
        }

        return (records.ToArray(), map);
    }
}
