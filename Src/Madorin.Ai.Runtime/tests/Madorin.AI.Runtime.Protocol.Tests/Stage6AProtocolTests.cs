using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage6AProtocolTests
{
    [TestMethod]
    public void ContextProjectionAdjustedEvent_MatchesVersionOneSnapshot()
    {
        var payload = new ContextProjectionAdjustedEvent(
            "invocation-1",
            DroppedMessageCount: 7,
            IncludedMessageCount: 3,
            EstimatedTokens: 4096,
            EstimateSource: "provider.estimated",
            Strategy: "TailWindow");

        var json = JsonSerializer.Serialize(
            payload,
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent);

        Assert.AreEqual(
            "{\"invocationId\":\"invocation-1\",\"droppedMessageCount\":7,\"includedMessageCount\":3,\"estimatedTokens\":4096,\"estimateSource\":\"provider.estimated\",\"strategy\":\"TailWindow\"}",
            json);
    }
}
