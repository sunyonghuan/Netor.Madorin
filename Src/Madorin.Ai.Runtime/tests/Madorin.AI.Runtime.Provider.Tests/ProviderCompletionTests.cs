using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderCompletionTests
{
    private readonly TestContext _testContext;

    public ProviderCompletionTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void ProviderExtensionData_EnforcesNamespaceAndSizeLimits()
    {
        using var smallValue = JsonDocument.Parse("\"value\"");
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProviderExtensionData("anthropic", "reasoning.signature", smallValue.RootElement));

        var oversizedJson = $"\"{new string('x', ProviderExtensionData.MaximumValueBytes)}\"";
        using var oversizedValue = JsonDocument.Parse(oversizedJson);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ProviderExtensionData(
                "provider.anthropic",
                "reasoning.signature",
                oversizedValue.RootElement));

        var extensions = Enumerable.Range(0, 5)
            .Select(index => ProviderExtensionData.CreateString(
                "provider.test",
                $"value.{index}",
                new string('x', 14_000)))
            .ToArray();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProviderExtensionData.ValidateCollection(extensions));
    }

    [TestMethod]
    public void ProviderExtensionPolicy_RedactsSensitiveFieldsAndBoundsRawResponses()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "authorization": "Bearer secret-key",
              "prompt": "sensitive prompt",
              "nested": { "api_key": "another-secret", "safe": "visible" }
            }
            """);

        var extension = ProviderExtensionPolicy.CreateRedactedJson(
            "compatible-profile",
            "raw.response",
            document.RootElement);
        var json = extension.Value.GetRawText();

        Assert.AreEqual("provider.compatible-profile", extension.Namespace);
        Assert.DoesNotContain("secret-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        Assert.Contains("visible", json, StringComparison.Ordinal);
        Assert.IsLessThanOrEqualTo(
            ProviderExtensionData.MaximumValueBytes,
            Encoding.UTF8.GetByteCount(json));
    }

    [TestMethod]
    public async Task AdapterMetadata_ExposesConfigurationTokenEstimateAndCancellationLimits()
    {
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(503, "{\"error\":\"not called\"}"),
            _testContext.CancellationToken);

        foreach (var kind in Enum.GetValues<ProviderAdapterKind>())
        {
            using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
            var schema = fixture.Adapter.ConfigurationSchema;
            Assert.IsFalse(string.IsNullOrWhiteSpace(schema.Version));
            Assert.IsNotEmpty(schema.Fields);
            Assert.IsFalse(fixture.Adapter.Capabilities.RemoteCancellation);

            var estimate = await fixture.Adapter.EstimateTokensAsync(
                ProviderAdapterTestFactory.CreateRequest(),
                _testContext.CancellationToken);
            Assert.AreEqual(ProviderUsageAccuracy.Estimated, estimate.Accuracy);
            Assert.IsNotNull(estimate.InputTokens);
            Assert.IsGreaterThan(0, estimate.InputTokens.Value);
            Assert.IsNull(estimate.OutputTokens);
        }

        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    public void RuntimeProviderRequest_RetryChangesRequestIdAndEnforcesSideEffectPolicy()
    {
        var request = ProviderAdapterTestFactory.CreateRequest();
        var firstAttempt = request.CreateAttempt(0);
        var secondAttempt = request.CreateAttempt(1);

        Assert.AreEqual(request.InvocationId, firstAttempt.InvocationId);
        Assert.AreEqual(request.InvocationId, secondAttempt.InvocationId);
        Assert.AreNotEqual(firstAttempt.InternalRequestId, secondAttempt.InternalRequestId);
        Assert.IsTrue(ProviderRetryPolicy.CanRetry(request, hasObservedOutput: false));
        Assert.IsFalse(ProviderRetryPolicy.CanRetry(request, hasObservedOutput: true));

        var unsafeRequest = request with { HasIrreversibleToolSideEffects = true };
        Assert.IsFalse(ProviderRetryPolicy.CanRetry(unsafeRequest, hasObservedOutput: false));
        Assert.ThrowsExactly<InvalidOperationException>(() => unsafeRequest.CreateAttempt(1));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompatibleProbe_ExplicitDocumentRecordsVersionTimeAndMissingCapabilities()
    {
        var response =
            """
            {
              "madorinCapabilities": {
                "streaming": true,
                "toolCalling": true,
                "reasoning": false,
                "usage": false,
                "remoteCancellation": false
              },
              "authorization": "Bearer response-secret",
              "prompt": "sensitive response prompt"
            }
            """;
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateCompatibleProbeAdapter(server.BaseAddress, "config-v7");
        var before = DateTimeOffset.UtcNow;

        var result = await adapter.ProbeCapabilitiesAsync(_testContext.CancellationToken);

        Assert.AreEqual("config-v7", result.ConfigurationVersion);
        Assert.AreEqual("endpoint", result.Source);
        Assert.IsGreaterThanOrEqualTo(before, result.ProbedAt);
        Assert.IsTrue(result.Capabilities.Streaming);
        Assert.IsTrue(result.Capabilities.ToolCalling);
        Assert.IsFalse(result.Capabilities.Reasoning);
        Assert.IsFalse(result.Capabilities.Usage);
        Assert.Contains("reasoning", result.MissingCapabilities);
        Assert.Contains("usage", result.MissingCapabilities);
        Assert.Contains("remote_cancellation", result.MissingCapabilities);

        var extension = Assert.ContainsSingle(result.Extensions!);
        var raw = extension.Value.GetRawText();
        Assert.DoesNotContain("response-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response prompt", raw, StringComparison.Ordinal);
        Assert.Contains("[redacted]", raw, StringComparison.Ordinal);

        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/v1/models", request.Path);
        Assert.AreEqual(
            $"Bearer {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["Authorization"]);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompatibleProbe_OrdinaryModelsResponseUsesExplicitDeclaredCapabilities()
    {
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(200, "{\"data\":[]}"),
            _testContext.CancellationToken);
        using var adapter = CreateCompatibleProbeAdapter(server.BaseAddress, "config-v2");

        var result = await adapter.ProbeCapabilitiesAsync(_testContext.CancellationToken);

        Assert.AreEqual("declared", result.Source);
        Assert.IsTrue(result.Capabilities.Streaming);
        Assert.IsFalse(result.Capabilities.ToolCalling);
        Assert.IsFalse(result.Capabilities.Reasoning);
        Assert.IsFalse(result.Capabilities.Usage);
        Assert.Contains("tool_calling", result.MissingCapabilities);
        Assert.Contains("reasoning", result.MissingCapabilities);
        Assert.Contains("usage", result.MissingCapabilities);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_BlobReferenceMapsToProviderData(
        ProviderAdapterKind kind)
    {
        var bytes = Encoding.UTF8.GetBytes("blob-payload-marker");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var blob = new BlobReference(
            "blob-1",
            bytes.Length,
            sha256,
            "image/png",
            "run:run-1",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var resolver = new FakeBlobResolver(
            new ResolvedProviderBlob(bytes, "image/png", "image.png"));
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(
            kind,
            server.BaseAddress,
            ProviderAdapterTestFactory.CreateCapabilities(),
            resolver);
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages =
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("inspect image"), new BlobRefContentBlock(blob)])
            ]
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.AreEqual(1, resolver.ResolveCount);
        var captured = Assert.ContainsSingle(server.Requests);
        Assert.Contains(Convert.ToBase64String(bytes), captured.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_MissingUsageProducesUnknownInsteadOfZero()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.CompatibleTextWithoutUsage(),
            _testContext.CancellationToken);
        var capabilities = ProviderAdapterTestFactory.CreateCapabilities(usage: false);
        using var fixture = ProviderAdapterTestFactory.Create(
            ProviderAdapterKind.OpenAICompatible,
            server.BaseAddress,
            capabilities);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var usage = Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.AreEqual(ProviderUsageAccuracy.Unknown, usage.Accuracy);
        Assert.IsNull(usage.InputTokens);
        Assert.IsNull(usage.OutputTokens);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI, "provider.anthropic")]
    [DataRow(ProviderAdapterKind.Anthropic, "provider.openai")]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_ForeignReasoningSignatureEmitsProjectionAdjustment(
        ProviderAdapterKind kind,
        string foreignNamespace)
    {
        var extension = ProviderExtensionData.CreateString(
            foreignNamespace,
            ProviderExtensionPolicy.ReasoningSignatureName,
            "foreign-signature-marker");
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        var request = ProviderAdapterTestFactory.CreateRequest() with
        {
            Messages =
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.Assistant,
                    [new ReasoningContentBlock("prior reasoning", [extension])]),
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("continue")])
            ]
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            _testContext.CancellationToken);

        var adjustment = Assert.ContainsSingle(events.OfType<ProjectionAdjustedProviderEvent>());
        Assert.AreEqual("provider_extension_omitted", adjustment.Code);
        Assert.AreEqual(foreignNamespace, adjustment.ExtensionNamespace);
        Assert.AreEqual(ProviderExtensionPolicy.ReasoningSignatureName, adjustment.ExtensionName);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        Assert.Contains("prior reasoning", captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-signature-marker", captured.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task AnthropicReasoningSignature_IsPreservedAsProviderExtension()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.Reasoning(ProviderAdapterKind.Anthropic),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(
            ProviderAdapterKind.Anthropic,
            server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var extensions = events
            .OfType<ReasoningDeltaProviderEvent>()
            .SelectMany(static reasoning => reasoning.ProviderExtensions ?? [])
            .ToArray();
        var signature = Assert.ContainsSingle(extensions);
        Assert.AreEqual("provider.anthropic", signature.Namespace);
        Assert.AreEqual(ProviderExtensionPolicy.ReasoningSignatureName, signature.Name);
        Assert.AreEqual("anthropic-signature-marker", signature.Value.GetString());
    }

    private static OpenAICompatibleProviderAdapter CreateCompatibleProbeAdapter(
        Uri baseAddress,
        string configurationVersion)
    {
        var options = new OpenAICompatibleProviderOptions(
            "compatible-profile",
            new Uri(baseAddress, "v1").AbsoluteUri,
            ProviderAdapterTestFactory.ApiKey,
            Capabilities: new ProviderCapabilities(Streaming: true),
            Models: [new ProviderModel(ProviderAdapterTestFactory.ModelId, "Test model")],
            ConfigurationVersion: configurationVersion,
            ProbePath: "models");
        return new OpenAICompatibleProviderAdapter(
            options,
            ProviderHttpClientFactory.Shared.CreateClient("compatible-profile"),
            disposeHttpClient: true);
    }

    private sealed class FakeBlobResolver(ResolvedProviderBlob result) : IProviderBlobResolver
    {
        public int ResolveCount { get; private set; }

        public ValueTask<ResolvedProviderBlob> ResolveAsync(
            BlobReference blob,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolveCount++;
            return ValueTask.FromResult(result);
        }
    }
}
