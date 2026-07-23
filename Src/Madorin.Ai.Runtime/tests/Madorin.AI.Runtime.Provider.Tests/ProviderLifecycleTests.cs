using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderLifecycleTests
{
    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    public async Task MultipleInvocations_UsesHttpClientFactoryOnce(ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(503, "{\"error\":\"unavailable\"}"));
        var httpClient = new HttpClient { BaseAddress = server.BaseAddress };
        var factory = new CountingHttpClientFactory(httpClient);
        ProviderModel[] models =
            [new(ProviderAdapterTestFactory.ModelId, "Test model")];
        IRuntimeProviderAdapter adapter = kind switch
        {
            ProviderAdapterKind.OpenAI => new OpenAIProviderAdapter(
                "openai-profile",
                ProviderAdapterTestFactory.ApiKey,
                new Uri(server.BaseAddress, "v1").AbsoluteUri,
                factory,
                models),
            ProviderAdapterKind.Anthropic => new AnthropicProviderAdapter(
                "anthropic-profile",
                ProviderAdapterTestFactory.ApiKey,
                server.BaseAddress.AbsoluteUri,
                factory,
                models),
            ProviderAdapterKind.OpenAICompatible => new OpenAICompatibleProviderAdapter(
                "compatible",
                new Uri(server.BaseAddress, "v1").AbsoluteUri,
                ProviderAdapterTestFactory.ApiKey,
                factory,
                models),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        using var adapterLifetime = adapter as IDisposable;

        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest());
        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest());

        Assert.AreEqual(1, factory.CreateCount);
        Assert.HasCount(2, server.Requests);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    public async Task UpdateCredentialsAsync_NextInvocationUsesReplacementCredential(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(401, "{\"error\":\"invalid credential\"}"));
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        var updater = Assert.IsInstanceOfType<IRuntimeProviderCredentialUpdater>(fixture.Adapter);

        await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest());
        await updater.UpdateCredentialsAsync(new CredentialsUpdateParameters(
            "run-1",
            fixture.Adapter.ProviderId,
            "replacement-key"));
        await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest());

        Assert.HasCount(2, server.Requests);
        AssertCredential(kind, ProviderAdapterTestFactory.ApiKey, server.Requests[0]);
        AssertCredential(kind, "replacement-key", server.Requests[1]);
    }

    private static void AssertCredential(
        ProviderAdapterKind kind,
        string expected,
        FakeProviderRequest request)
    {
        var headerName = kind == ProviderAdapterKind.Anthropic
            ? "x-api-key"
            : "Authorization";
        Assert.IsTrue(request.Headers.TryGetValue(headerName, out var actual));
        Assert.AreEqual(
            kind == ProviderAdapterKind.Anthropic ? expected : $"Bearer {expected}",
            actual);
    }

    private sealed class CountingHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            return httpClient;
        }
    }
}
