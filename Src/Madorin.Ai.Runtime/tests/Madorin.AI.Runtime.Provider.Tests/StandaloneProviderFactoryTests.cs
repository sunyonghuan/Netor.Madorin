using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class StandaloneProviderFactoryTests
{
    private readonly TestContext _testContext;

    public StandaloneProviderFactoryTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Create_ConfiguredProfile_ReusesCorrectAdapterAndForwardsConfiguration(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        var providerId = $"profile-{kind}";
        var protocol = kind switch
        {
            ProviderAdapterKind.OpenAI => "OpenAI",
            ProviderAdapterKind.Anthropic => "Anthropic",
            ProviderAdapterKind.OpenAICompatible => "OpenAI Compatible",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var baseUrl = kind is ProviderAdapterKind.OpenAI or ProviderAdapterKind.OpenAICompatible
            ? new Uri(server.BaseAddress, "v1").AbsoluteUri
            : server.BaseAddress.AbsoluteUri;
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = providerId,
                    Protocol = protocol,
                    BaseUrl = baseUrl,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    AuthenticationHeader = "X-Provider-Key",
                    AuthenticationScheme = "Token",
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        var resolver = StandaloneProviderFactory.Create(config);

        var adapter = resolver(CreateSelection(providerId));
        using var adapterLifetime = adapter as IDisposable;
        var cachedAdapter = resolver(CreateSelection(providerId));

        Assert.AreSame(adapter, cachedAdapter);
        Assert.AreEqual(providerId, adapter.ProviderId);
        Assert.HasCount(1, adapter.Models);
        Assert.AreEqual(ProviderAdapterTestFactory.ModelId, adapter.Models[0].Id);
        AssertAdapterType(kind, adapter);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.EndsWith(ProviderProtocolScripts.ExpectedPathSuffix(kind), request.Path);
        Assert.Contains(ProviderAdapterTestFactory.ModelId, request.Body, StringComparison.Ordinal);
        AssertAuthentication(kind, request.Headers);
    }

    private static NextTurnSelection CreateSelection(string providerId) =>
        new(
            1,
            RuntimeMode.Expert,
            new DefaultSelection(providerId, ProviderAdapterTestFactory.ModelId),
            new ExpertModeOptions(new AgentRef("agent", "v1", "test")));

    private static void AssertAdapterType(
        ProviderAdapterKind kind,
        object adapter)
    {
        switch (kind)
        {
            case ProviderAdapterKind.OpenAI:
                Assert.IsInstanceOfType<OpenAIProviderAdapter>(adapter);
                break;
            case ProviderAdapterKind.Anthropic:
                Assert.IsInstanceOfType<AnthropicProviderAdapter>(adapter);
                break;
            case ProviderAdapterKind.OpenAICompatible:
                Assert.IsInstanceOfType<OpenAICompatibleProviderAdapter>(adapter);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void AssertAuthentication(
        ProviderAdapterKind kind,
        IReadOnlyDictionary<string, string> headers)
    {
        var headerName = kind switch
        {
            ProviderAdapterKind.Anthropic => "x-api-key",
            ProviderAdapterKind.OpenAICompatible => "X-Provider-Key",
            _ => "Authorization"
        };
        var expected = kind switch
        {
            ProviderAdapterKind.Anthropic => ProviderAdapterTestFactory.ApiKey,
            ProviderAdapterKind.OpenAICompatible => $"Token {ProviderAdapterTestFactory.ApiKey}",
            _ => $"Bearer {ProviderAdapterTestFactory.ApiKey}"
        };

        Assert.IsTrue(headers.TryGetValue(headerName, out var actual));
        Assert.AreEqual(expected, actual);
    }
}
