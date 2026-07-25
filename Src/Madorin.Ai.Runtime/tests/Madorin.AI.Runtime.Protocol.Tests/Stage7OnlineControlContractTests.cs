using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage7OnlineControlContractTests
{
    [TestMethod]
    public void MessageTypes_OnlineControlMethods_AreStable()
    {
        Assert.AreEqual("runtime.status", GetMessageType(nameof(MessageTypes.RuntimeStatus)));
        Assert.AreEqual("run.list", GetMessageType(nameof(MessageTypes.RunList)));
        Assert.AreEqual("run.cancel", GetMessageType(nameof(MessageTypes.RunCancel)));
    }

    [TestMethod]
    public void RuntimeStatus_SourceGeneratedJson_RoundTrips()
    {
        var expected = new RuntimeStatusResult(
            "runtime-1",
            "1.2.3",
            "1.1",
            @"E:\workspace 中文",
            4242,
            new DateTimeOffset(2026, 7, 25, 1, 2, 3, TimeSpan.Zero),
            ActiveRunCount: 2,
            ConnectedHostCount: 3,
            ConnectedEventChannelCount: 1);

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.RuntimeStatusResult);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RuntimeStatusResult);

        Assert.AreEqual(expected, actual);
        Assert.Contains("\"runtimeInstanceId\"", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(
            @"E:\workspace 中文",
            document.RootElement.GetProperty("workspace").GetString());
    }

    [TestMethod]
    public void RunListAndCancel_SourceGeneratedJson_RoundTrip()
    {
        var startedAt = new DateTimeOffset(2026, 7, 25, 4, 5, 6, TimeSpan.Zero);
        var expected = new RunListResult(
        [
            new RunListItem("run-1", "session-1", RunStatus.WaitingForCredentials, startedAt)
        ]);

        var json = JsonSerializer.Serialize(expected, RuntimeJsonContext.Default.RunListResult);
        var actual = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.RunListResult);
        Assert.IsNotNull(actual);
        var run = Assert.ContainsSingle(actual.Runs);

        Assert.AreEqual("run-1", run.RunId);
        Assert.AreEqual(RunStatus.WaitingForCredentials, run.Status);
        Assert.AreEqual(startedAt, run.StartedAt);

        var cancel = new RunCancelParameters("run-1", "用户请求取消");
        var cancelJson = JsonSerializer.Serialize(
            cancel,
            RuntimeJsonContext.Default.RunCancelParameters);
        Assert.AreEqual(
            cancel,
            JsonSerializer.Deserialize(
                cancelJson,
                RuntimeJsonContext.Default.RunCancelParameters));
    }

    [TestMethod]
    public void OnlineControlParameters_SourceGeneratedMetadata_IsAvailable()
    {
        Assert.IsNotNull(RuntimeJsonContext.Default.RuntimeStatusParameters);
        Assert.IsNotNull(RuntimeJsonContext.Default.RunListParameters);
        Assert.IsNotNull(RuntimeJsonContext.Default.RunListItemArray);
    }

    private static object? GetMessageType(string fieldName) =>
        typeof(MessageTypes).GetField(fieldName)?.GetRawConstantValue();
}
