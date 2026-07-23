using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class RuntimeJsonContextTests
{
    [TestMethod]
    public void RuntimeVersionInfo_RoundTripsWithSourceGeneratedMetadata()
    {
        var expected = new RuntimeVersionInfo(
            "Madorin.AI.Runtime",
            "0.1.0",
            ProtocolVersions.Current,
            ".NET 10.0",
            "Windows",
            "x64");

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.RuntimeVersionInfo);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RuntimeVersionInfo);

        Assert.AreEqual(expected, actual);
        StringAssert.Contains(json, $"\"protocolVersion\":\"{ProtocolVersions.Current}\"");
    }

    [TestMethod]
    public void InitializeRequest_RoundTripsWithCamelCaseProperties()
    {
        var expected = new InitializeRequest(
            "host-1",
            "1.0.0",
            ["1.0", "0.9"],
            new RuntimeCapabilities(),
            42);

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.InitializeRequest);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.InitializeRequest);

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.HostInstanceId, actual.HostInstanceId);
        Assert.AreEqual(expected.LastConfirmedGsn, actual.LastConfirmedGsn);
        CollectionAssert.AreEqual(
            expected.SupportedProtocolVersions.ToArray(),
            actual.SupportedProtocolVersions.ToArray());
        StringAssert.Contains(json, "\"hostInstanceId\":\"host-1\"");
        StringAssert.Contains(json, "\"lastConfirmedGsn\":42");
    }

    [TestMethod]
    public void NewSessionRunRequest_RoundTripsPolymorphicContentAndStringEnum()
    {
        var expected = new NewSessionRunRequest(
            "session-key-1",
            "run-key-1",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("provider-1", "model-1"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "System prompt"))),
            [new TextContentBlock("Hello")]);

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        Assert.AreEqual(RuntimeMode.Expert, actual.Mode);
        Assert.IsInstanceOfType<ExpertModeOptions>(actual.Selection.ModeOptions);
        Assert.HasCount(1, actual.InitialInput);
        var textBlock = actual.InitialInput[0] as TextContentBlock;
        Assert.IsNotNull(textBlock);
        Assert.AreEqual("Hello", textBlock.Text);
        StringAssert.Contains(json, "\"sessionIdempotencyKey\":\"session-key-1\"");
        StringAssert.Contains(json, "\"mode\":\"Expert\"");
        StringAssert.Contains(json, "\"type\":\"text\"");
    }

    [TestMethod]
    public void RuntimeEventEnvelope_RoundTripsWithGsnAndTimestamp()
    {
        using var document = JsonDocument.Parse("""{"delta":"Hello"}""");
        var timestamp = new DateTimeOffset(2026, 7, 21, 10, 30, 0, TimeSpan.Zero);
        var expected = new RuntimeEventEnvelope(
            "runtime-1",
            7,
            "run-1",
            3,
            MessageTypes.TextDelta,
            timestamp,
            document.RootElement.Clone());

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.RuntimeEventEnvelope);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RuntimeEventEnvelope);

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.RuntimeInstanceId, actual.RuntimeInstanceId);
        Assert.AreEqual(expected.Gsn, actual.Gsn);
        Assert.AreEqual(expected.Timestamp, actual.Timestamp);
        Assert.AreEqual("Hello", actual.Payload.GetProperty("delta").GetString());
        StringAssert.Contains(json, "\"runtimeInstanceId\":\"runtime-1\"");
        StringAssert.Contains(json, "\"gsn\":7");
        StringAssert.Contains(json, "\"timestamp\":");
        Assert.IsFalse(json.Contains("globalSequence", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InitializeRequest_WithUnknownProperty_IgnoresUnknownProperty()
    {
        const string json =
            """
            {
              "hostInstanceId": "host-1",
              "clientVersion": "1.0.0",
              "supportedProtocolVersions": ["1.0"],
              "expectedCapabilities": {
                "streaming": true,
                "toolCalling": true,
                "blobTransfer": true,
                "multiRun": true,
                "sessionResume": true
              },
              "lastConfirmedGsn": 42,
              "futureOptionalField": { "enabled": true }
            }
            """;

        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.InitializeRequest);

        Assert.IsNotNull(actual);
        Assert.AreEqual("host-1", actual.HostInstanceId);
        Assert.AreEqual(42, actual.LastConfirmedGsn);
    }

    [TestMethod]
    public void ProtocolEnums_UnknownValue_ThrowsJsonException()
    {
        var runtimeModeJson = JsonSerializer.Serialize(
            RuntimeMode.Expert,
            RuntimeJsonContext.Default.RuntimeMode);
        var runStatusJson = JsonSerializer.Serialize(
            RunStatus.WaitingForTool,
            RuntimeJsonContext.Default.RunStatus);

        Assert.AreEqual("\"Expert\"", runtimeModeJson);
        Assert.AreEqual("\"WaitingForTool\"", runStatusJson);
        Assert.ThrowsExactly<JsonException>(() =>
        {
            JsonSerializer.Deserialize(
                "\"FutureMode\"",
                RuntimeJsonContext.Default.RuntimeMode);
        });
        Assert.ThrowsExactly<JsonException>(() =>
        {
            JsonSerializer.Deserialize(
                "\"FutureStatus\"",
                RuntimeJsonContext.Default.RunStatus);
        });
    }

    [TestMethod]
    public void ProtocolVersions_Negotiate_SelectsIntersectionOrReturnsNull()
    {
        Assert.AreEqual(
            ProtocolVersions.Current,
            ProtocolVersions.Negotiate(["2.0", ProtocolVersions.Current]));
        Assert.IsNull(ProtocolVersions.Negotiate(["2.0", "0.9"]));
        Assert.AreEqual(
            ProtocolVersions.Legacy,
            ProtocolVersions.Negotiate([ProtocolVersions.Legacy]));
        CollectionAssert.AreEqual(
            new[] { ProtocolVersions.Current, ProtocolVersions.Legacy },
            ProtocolVersions.Supported);
    }
}
