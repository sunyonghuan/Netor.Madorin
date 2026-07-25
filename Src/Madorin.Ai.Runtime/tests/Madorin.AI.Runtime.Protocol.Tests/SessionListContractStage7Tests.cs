using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class SessionListContractStage7Tests
{
    [TestMethod]
    public void MessageTypes_SessionList_IsSessionList()
    {
        var fieldValue = typeof(MessageTypes)
            .GetField(nameof(MessageTypes.SessionList))!
            .GetRawConstantValue();

        Assert.AreEqual("session.list", fieldValue);
    }

    [TestMethod]
    public void SessionListParameters_Defaults_AreStable()
    {
        var parameters = new SessionListParameters();

        Assert.IsNull(parameters.Mode);
        Assert.AreEqual(SessionStatus.Active, parameters.Status);
        Assert.IsNull(parameters.Since);
        Assert.IsNull(parameters.Search);
        Assert.AreEqual(20, parameters.Limit);
        Assert.IsNull(parameters.Cursor);
    }

    [TestMethod]
    public void SessionListParameters_FullContract_RoundTrips()
    {
        var since = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        var parameters = new SessionListParameters(
            Mode: RuntimeMode.Meeting,
            Status: SessionStatus.Archived,
            Since: since,
            Search: "alpha",
            Limit: 50,
            Cursor: "cursor-abc");

        var json = JsonSerializer.Serialize(
            parameters,
            RuntimeJsonContext.Default.SessionListParameters);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.SessionListParameters);

        Assert.IsNotNull(actual);
        Assert.AreEqual(RuntimeMode.Meeting, actual.Mode);
        Assert.AreEqual(SessionStatus.Archived, actual.Status);
        Assert.AreEqual(since, actual.Since);
        Assert.AreEqual("alpha", actual.Search);
        Assert.AreEqual(50, actual.Limit);
        Assert.AreEqual("cursor-abc", actual.Cursor);
    }

    [TestMethod]
    public void SessionListParameters_JsonFieldNames_AreCamelCase()
    {
        var parameters = new SessionListParameters(
            Mode: RuntimeMode.Expert,
            Status: SessionStatus.Active,
            Since: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Search: "test",
            Limit: 10,
            Cursor: "cur");

        var json = JsonSerializer.Serialize(
            parameters,
            RuntimeJsonContext.Default.SessionListParameters);

        StringAssert.Contains(json, "\"mode\":");
        StringAssert.Contains(json, "\"status\":");
        StringAssert.Contains(json, "\"since\":");
        StringAssert.Contains(json, "\"search\":");
        StringAssert.Contains(json, "\"limit\":");
        StringAssert.Contains(json, "\"cursor\":");

        Assert.IsFalse(json.Contains("\"Mode\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"Status\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"Since\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"Search\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"Limit\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"Cursor\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionListResult_WithItem_RoundTrips()
    {
        var updatedAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var item = new SessionListItem(
            "session-1",
            RuntimeMode.Work,
            SessionStatus.Active,
            updatedAt,
            "My Session");
        var result = new SessionListResult(
            [item],
            "next-cursor");

        var json = JsonSerializer.Serialize(
            result,
            RuntimeJsonContext.Default.SessionListResult);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.SessionListResult);

        Assert.IsNotNull(actual);
        Assert.AreEqual("next-cursor", actual.NextCursor);
        Assert.HasCount(1, actual.Sessions);

        var actualItem = actual.Sessions[0];
        Assert.AreEqual("session-1", actualItem.SessionId);
        Assert.AreEqual(RuntimeMode.Work, actualItem.Mode);
        Assert.AreEqual(SessionStatus.Active, actualItem.Status);
        Assert.AreEqual(updatedAt, actualItem.UpdatedAt);
        Assert.AreEqual("My Session", actualItem.Title);
    }
}
