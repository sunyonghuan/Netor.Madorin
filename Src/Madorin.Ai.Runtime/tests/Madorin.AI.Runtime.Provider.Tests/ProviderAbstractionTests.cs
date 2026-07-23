using System.Text.Json;
using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderAbstractionTests
{
    [TestMethod]
    public async Task CompleteStreamingAsync_AdapterBoundaryReturnsNormalizedEvents()
    {
        IRuntimeProviderAdapter adapter = new FakeProviderAdapter();
        var events = new List<RuntimeProviderEvent>();

        var request = new RuntimeProviderRequest(
            "inv-1",
            "agent-1",
            "fake",
            "test-model",
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("hello")]),
            ]);

        await foreach (var providerEvent in adapter.CompleteStreamingAsync(request))
        {
            events.Add(providerEvent);
        }

        Assert.HasCount(2, events);
        Assert.IsInstanceOfType<TextDeltaProviderEvent>(events[0]);
        Assert.AreEqual("hello", ((TextDeltaProviderEvent)events[0]).Delta);
        Assert.IsInstanceOfType<InvocationCompletedProviderEvent>(events[1]);
    }

    [TestMethod]
    public void RuntimeProviderEvent_AllVariants_RoundTripPolymorphically()
    {
        RuntimeProviderEvent[] events =
        [
            new TextDeltaProviderEvent("inv-1", "hello"),
            new ReasoningDeltaProviderEvent(
                "inv-1",
                "thinking",
                [
                    ProviderExtensionData.CreateString(
                        "provider.anthropic",
                        ProviderExtensionPolicy.ReasoningSignatureName,
                        "signature")
                ]),
            new ToolCallDeltaProviderEvent("inv-1", "call-1", "get_weather", "{\"city\":"),
            new ToolCallCompleteProviderEvent(
                "inv-1",
                "call-1",
                "weather",
                "get_weather",
                "{\"city\":\"Shanghai\"}"),
            new UsageUpdatedProviderEvent("inv-1", 12, 8),
            new UsageUpdatedProviderEvent(
                "inv-1",
                null,
                null,
                ProviderUsageAccuracy.Unknown),
            new ProjectionAdjustedProviderEvent(
                "inv-1",
                "provider_extension_omitted",
                "provider.anthropic",
                "cache.reference"),
            new InvocationCompletedProviderEvent("inv-1", "stop"),
            new InvocationFailedProviderEvent(
                "inv-1",
                new RuntimeError(
                    "ProviderRateLimited",
                    "provider",
                    "Rate limited.",
                    true,
                    CreateJsonElement("{\"code\":\"rate_limit\"}"),
                    "diag-1")),
        ];

        foreach (var expected in events)
        {
            var json = JsonSerializer.Serialize(
                expected,
                RuntimeProviderJsonContext.Default.RuntimeProviderEvent);

            var actual = JsonSerializer.Deserialize(
                json,
                RuntimeProviderJsonContext.Default.RuntimeProviderEvent);

            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.GetType(), actual.GetType());
            Assert.AreEqual(expected.InvocationId, actual.InvocationId);
            Assert.AreEqual(
                json,
                JsonSerializer.Serialize(
                    actual,
                    RuntimeProviderJsonContext.Default.RuntimeProviderEvent));
        }
    }

    [TestMethod]
    public void RuntimeProviderRequest_AllSerializableFields_AreIncluded()
    {
        var request = new RuntimeProviderRequest(
            "inv-1",
            "agent-1",
            "openai",
            "gpt-5",
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.System,
                    [new TextContentBlock("You are an assistant.")]),
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [
                        new TextContentBlock("hello"),
                        new ToolCallContentBlock(
                            "call-1",
                            "weather",
                            "get_weather",
                            CreateJsonElement("{\"city\":\"Shanghai\"}")),
                    ]),
            ],
            [
                new ToolDescriptor(
                    "weather",
                    "host",
                    "Weather",
                    "Gets the current weather.",
                    "{\"type\":\"object\"}"),
            ],
            Temperature: 0.25f,
            MaxTokens: 1024,
            InternalRequestId: "request-1",
            AttemptNumber: 1,
            IsIdempotent: true,
            HasIrreversibleToolSideEffects: false,
            CancellationToken: new CancellationToken(canceled: true));

        var json = JsonSerializer.Serialize(
            request,
            RuntimeProviderJsonContext.Default.RuntimeProviderRequest);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual("inv-1", root.GetProperty("invocationId").GetString());
        Assert.AreEqual("agent-1", root.GetProperty("agentId").GetString());
        Assert.AreEqual("openai", root.GetProperty("providerId").GetString());
        Assert.AreEqual("gpt-5", root.GetProperty("modelId").GetString());
        Assert.AreEqual(2, root.GetProperty("messages").GetArrayLength());
        Assert.AreEqual(1, root.GetProperty("tools").GetArrayLength());
        Assert.AreEqual(0.25, root.GetProperty("temperature").GetDouble());
        Assert.AreEqual(1024, root.GetProperty("maxTokens").GetInt32());
        Assert.AreEqual("request-1", root.GetProperty("internalRequestId").GetString());
        Assert.AreEqual(1, root.GetProperty("attemptNumber").GetInt32());
        Assert.IsTrue(root.GetProperty("isIdempotent").GetBoolean());
        Assert.IsFalse(root.GetProperty("hasIrreversibleToolSideEffects").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("cancellationToken", out _));

        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeProviderJsonContext.Default.RuntimeProviderRequest);
        Assert.IsNotNull(actual);
        Assert.AreEqual(request.InvocationId, actual.InvocationId);
        Assert.AreEqual(request.AgentId, actual.AgentId);
        Assert.AreEqual(request.ProviderId, actual.ProviderId);
        Assert.AreEqual(request.ModelId, actual.ModelId);
        Assert.HasCount(2, actual.Messages);
        Assert.AreEqual(RuntimeProviderRoles.System, actual.Messages[0].Role);
        Assert.HasCount(1, actual.Messages[0].Content);
        Assert.AreEqual(RuntimeProviderRoles.User, actual.Messages[1].Role);
        Assert.HasCount(2, actual.Messages[1].Content);
        Assert.IsNotNull(actual.Tools);
        Assert.HasCount(1, actual.Tools);
        Assert.AreEqual(request.Temperature, actual.Temperature);
        Assert.AreEqual(request.MaxTokens, actual.MaxTokens);
        Assert.AreEqual(request.InternalRequestId, actual.InternalRequestId);
        Assert.AreEqual(request.AttemptNumber, actual.AttemptNumber);
        Assert.AreEqual(request.IsIdempotent, actual.IsIdempotent);
        Assert.AreEqual(
            request.HasIrreversibleToolSideEffects,
            actual.HasIrreversibleToolSideEffects);
        Assert.AreEqual(default, actual.CancellationToken);
    }

    [TestMethod]
    public void ProviderCapabilities_Serialization_ContainsCompleteStructure()
    {
        var capabilities = new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true,
            Vision: true,
            Audio: false,
            StructuredOutput: true,
            Reasoning: true,
            PromptCaching: false,
            Files: true,
            ComputerUse: false,
            Embeddings: true);

        var json = JsonSerializer.Serialize(
            capabilities,
            RuntimeProviderJsonContext.Default.ProviderCapabilities);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(12, root.EnumerateObject().Count());
        Assert.IsTrue(root.GetProperty("streaming").GetBoolean());
        Assert.IsTrue(root.GetProperty("toolCalling").GetBoolean());
        Assert.IsTrue(root.GetProperty("vision").GetBoolean());
        Assert.IsFalse(root.GetProperty("audio").GetBoolean());
        Assert.IsTrue(root.GetProperty("structuredOutput").GetBoolean());
        Assert.IsTrue(root.GetProperty("reasoning").GetBoolean());
        Assert.IsFalse(root.GetProperty("promptCaching").GetBoolean());
        Assert.IsTrue(root.GetProperty("files").GetBoolean());
        Assert.IsFalse(root.GetProperty("computerUse").GetBoolean());
        Assert.IsTrue(root.GetProperty("embeddings").GetBoolean());
        Assert.IsFalse(root.GetProperty("usage").GetBoolean());
        Assert.IsFalse(root.GetProperty("remoteCancellation").GetBoolean());
    }

    private static JsonElement CreateJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeProviderAdapter : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var text = request.Messages
                .FirstOrDefault(static message => message.Role == RuntimeProviderRoles.User)?
                .Content
                .OfType<TextContentBlock>()
                .FirstOrDefault()?
                .Text ?? string.Empty;
            yield return new TextDeltaProviderEvent(request.InvocationId, text);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
