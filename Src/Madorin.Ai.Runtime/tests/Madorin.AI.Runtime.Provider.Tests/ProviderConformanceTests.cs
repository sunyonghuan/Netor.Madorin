using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderConformanceTests
{
    private readonly TestContext _testContext;

    public ProviderConformanceTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI, false)]
    [DataRow(ProviderAdapterKind.Anthropic, false)]
    [DataRow(ProviderAdapterKind.OpenAICompatible, false)]
    [DataRow(ProviderAdapterKind.OpenAI, true)]
    [DataRow(ProviderAdapterKind.Anthropic, true)]
    [DataRow(ProviderAdapterKind.OpenAICompatible, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_NormalAndFragmentedStreams_ProduceCanonicalEvents(
        ProviderAdapterKind kind,
        bool fragmented)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind, fragmented),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var text = string.Concat(events.OfType<TextDeltaProviderEvent>().Select(static item => item.Delta));
        Assert.AreEqual("Hello", text);
        var usage = Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.AreEqual(5, usage.InputTokens);
        Assert.AreEqual(2, usage.OutputTokens);
        var completed = Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsFalse(string.IsNullOrWhiteSpace(completed.FinishReason));
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());

        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("POST", request.Method);
        Assert.EndsWith(ProviderProtocolScripts.ExpectedPathSuffix(kind), request.Path);
        Assert.Contains(ProviderAdapterTestFactory.ModelId, request.Body, StringComparison.Ordinal);
        Assert.Contains("sensitive prompt", request.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ToolCall_ProducesCompleteCanonicalArguments(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Gets the current weather.",
                "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}")
        ];

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        var delta = Assert.ContainsSingle(events.OfType<ToolCallDeltaProviderEvent>());
        Assert.AreEqual("call-1", delta.CallId);
        Assert.AreEqual("get_weather", delta.Name);
        var completed = Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.AreEqual("call-1", completed.CallId);
        Assert.AreEqual("weather", completed.ToolId);
        Assert.AreEqual("get_weather", completed.Name);
        Assert.AreEqual("{\"city\":\"Shanghai\"}", completed.ArgumentsJson);
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());

        var request = Assert.ContainsSingle(server.Requests);
        Assert.Contains("get_weather", request.Body, StringComparison.Ordinal);
        Assert.Contains("required", request.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ReasoningStream_ProducesCanonicalReasoning(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.Reasoning(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var reasoning = string.Concat(
            events.OfType<ReasoningDeltaProviderEvent>().Select(static item => item.Delta));
        Assert.AreEqual("think", reasoning);
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_StructuredOutput_ForwardsJsonSchema(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "structured_marker": { "type": "string" }
              },
              "required": ["structured_marker"],
              "additionalProperties": false
            }
            """);
        var structuredOutput = new RuntimeStructuredOutput(
            "answer_contract",
            schema.RootElement.Clone(),
            "Canonical answer contract.");

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(structuredOutput: structuredOutput),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        if (kind is not ProviderAdapterKind.Anthropic)
        {
            Assert.Contains("answer_contract", request.Body, StringComparison.Ordinal);
        }

        Assert.Contains("structured_marker", request.Body, StringComparison.Ordinal);
        Assert.Contains("required", request.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ToolHistory_ForwardsCallAndResult(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        using var arguments = JsonDocument.Parse("{\"city\":\"history-city-marker\"}");
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Gets the current weather.",
                "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}")
        ];
        RuntimeProviderMessage[] messages =
        [
            new(RuntimeProviderRoles.User, [new TextContentBlock("weather question")]),
            new(
                RuntimeProviderRoles.Assistant,
                [
                    new ToolCallContentBlock(
                        "history-call-marker",
                        "weather",
                        "get_weather",
                        arguments.RootElement.Clone())
                ]),
            new(
                RuntimeProviderRoles.Tool,
                [
                    new ToolResultContentBlock(
                        "history-call-marker",
                        "weather",
                        true,
                        [new TextContentBlock("history-result-marker")])
                ])
        ];
        var request = ProviderAdapterTestFactory.CreateRequest(tools) with
        {
            Messages = messages
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        Assert.Contains("history-call-marker", captured.Body, StringComparison.Ordinal);
        Assert.Contains("get_weather", captured.Body, StringComparison.Ordinal);
        Assert.Contains("history-city-marker", captured.Body, StringComparison.Ordinal);
        Assert.Contains("history-result-marker", captured.Body, StringComparison.Ordinal);
    }
}
