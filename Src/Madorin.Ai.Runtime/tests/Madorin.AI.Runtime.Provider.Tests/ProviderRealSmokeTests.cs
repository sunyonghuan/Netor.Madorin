using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
[TestCategory("External")]
public sealed class ProviderRealSmokeTests
{
    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConfiguredProvider_ReturnsTerminalEvent(ProviderAdapterKind kind)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var prefix = kind switch
        {
            ProviderAdapterKind.OpenAI => "MADORIN_OPENAI",
            ProviderAdapterKind.Anthropic => "MADORIN_ANTHROPIC",
            ProviderAdapterKind.OpenAICompatible => "MADORIN_COMPATIBLE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable($"{prefix}_BASE_URL");
        var modelId = Environment.GetEnvironmentVariable($"{prefix}_MODEL_ID");
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(modelId))
        {
            Assert.Inconclusive($"{prefix}_API_KEY, _BASE_URL, and _MODEL_ID are required.");
        }

        ProviderModel[] models =
        [
            new(
                modelId,
                modelId,
                Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
        ];
        IRuntimeProviderAdapter adapter = kind switch
        {
            ProviderAdapterKind.OpenAI => new OpenAIProviderAdapter(apiKey, baseUrl, models),
            ProviderAdapterKind.Anthropic => new AnthropicProviderAdapter(apiKey, baseUrl, models),
            ProviderAdapterKind.OpenAICompatible => new OpenAICompatibleProviderAdapter(
                new OpenAICompatibleProviderOptions(
                    "compatible-smoke",
                    baseUrl,
                    apiKey,
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities(),
                    Models: models),
                ProviderHttpClientFactory.Shared.CreateClient("compatible-smoke"),
                disposeHttpClient: true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        using var adapterLifetime = adapter as IDisposable;
        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "smoke-agent",
            adapter.ProviderId,
            modelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("Reply with OK.")])
            ],
            MaxTokens: 16);

        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request);

        Assert.IsTrue(events.Any(static item => item is TextDeltaProviderEvent));
        Assert.IsTrue(events.Any(static item => item is InvocationCompletedProviderEvent));
        Assert.IsFalse(events.Any(static item => item is InvocationFailedProviderEvent));
    }
}
