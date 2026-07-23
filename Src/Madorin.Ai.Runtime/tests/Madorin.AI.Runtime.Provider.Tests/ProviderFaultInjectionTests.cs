using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderFaultInjectionTests
{
    private readonly TestContext _testContext;

    public ProviderFaultInjectionTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_SlowStream_CompletesWithoutLosingEvents(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.SlowText(kind, TimeSpan.FromMilliseconds(25)),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual(
            "Hello",
            string.Concat(events.OfType<TextDeltaProviderEvent>().Select(static item => item.Delta)));
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI, false)]
    [DataRow(ProviderAdapterKind.Anthropic, false)]
    [DataRow(ProviderAdapterKind.OpenAICompatible, false)]
    [DataRow(ProviderAdapterKind.OpenAI, true)]
    [DataRow(ProviderAdapterKind.Anthropic, true)]
    [DataRow(ProviderAdapterKind.OpenAICompatible, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_InvalidOrDisconnectedStream_PreservesDeltaThenFails(
        ProviderAdapterKind kind,
        bool disconnect)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => disconnect
                ? ProviderProtocolScripts.Disconnected(kind)
                : ProviderProtocolScripts.InvalidJson(kind),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual(
            "Hel",
            string.Concat(events.OfType<TextDeltaProviderEvent>().Select(static item => item.Delta)));
        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        if (disconnect)
        {
            Assert.IsTrue(
                failure.Error.Code is RuntimeErrorCodes.ProviderProtocolError
                    or RuntimeErrorCodes.ProviderRequestFailed);
        }
        else
        {
            Assert.AreEqual(RuntimeErrorCodes.ProviderProtocolError, failure.Error.Code);
        }

        Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_CancelAfterDelta_ThrowsAndKeepsObservedDelta(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.SlowText(kind, TimeSpan.FromSeconds(5)),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        await using var enumerator = fixture.Adapter
            .CompleteStreamingAsync(ProviderAdapterTestFactory.CreateRequest(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var observed = new List<RuntimeProviderEvent>();

        while (await enumerator.MoveNextAsync())
        {
            observed.Add(enumerator.Current);
            if (enumerator.Current is TextDeltaProviderEvent)
            {
                break;
            }
        }

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await enumerator.MoveNextAsync();
        });
        Assert.AreEqual(
            "Hel",
            string.Concat(observed.OfType<TextDeltaProviderEvent>().Select(static item => item.Delta)));
        Assert.IsEmpty(observed.OfType<InvocationFailedProviderEvent>());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_HttpTimeout_ReturnsTimeoutFailure()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.DelayedText(
                ProviderAdapterKind.OpenAICompatible,
                TimeSpan.FromSeconds(2)),
            _testContext.CancellationToken);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var capabilities = ProviderAdapterTestFactory.CreateCapabilities();
        using var adapter = new OpenAICompatibleProviderAdapter(
            new OpenAICompatibleProviderOptions(
                "compatible",
                new Uri(server.BaseAddress, "v1").AbsoluteUri,
                ProviderAdapterTestFactory.ApiKey,
                Capabilities: capabilities,
                Models:
                [
                    new ProviderModel(
                        ProviderAdapterTestFactory.ModelId,
                        "Test model",
                        Capabilities: capabilities)
                ]),
            httpClient);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.AreEqual(RuntimeErrorCodes.ProviderTimeout, failure.Error.Code);
        Assert.AreEqual("timeout", failure.Error.Category);
        Assert.IsTrue(failure.Error.IsRetryable);
        Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
    }
}
