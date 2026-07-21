using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderAbstractionTests
{
    [TestMethod]
    public async Task CompleteStreamingAsync_AdapterBoundaryReturnsNormalizedEvents()
    {
        IRuntimeProviderAdapter adapter = new FakeProviderAdapter();
        var events = new List<RuntimeProviderEvent>();

        await foreach (var providerEvent in adapter.CompleteStreamingAsync(
                           new RuntimeProviderRequest(
                               "test-model",
                               [new RuntimeProviderMessage("user", "hello")])))
        {
            events.Add(providerEvent);
        }

        Assert.HasCount(1, events);
        Assert.AreEqual("output.delta", events[0].Type);
    }

    private sealed class FakeProviderAdapter : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(
            SupportsStreaming: true,
            SupportsTools: false,
            SupportsReasoning: false);

        public ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderModel> models = [new("test-model", "Test Model")];
            return ValueTask.FromResult(models);
        }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new RuntimeProviderEvent(
                "output.delta",
                request.Messages[0].Content,
                null,
                null);
        }
    }
}
