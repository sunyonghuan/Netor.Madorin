using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderAgentInvocationExecutorTests
{
    [TestMethod]
    public async Task ExecuteAsync_StructuredOutput_ForwardsSameDescriptor()
    {
        using var schemaDoc = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""");
        var schema = schemaDoc.RootElement.Clone();

        var structuredOutput = new RuntimeStructuredOutput("test_output", schema, "A test schema");

        var snapshot = new InvocationSnapshot(
            "inv-1", "agent-1", "fake", "model-1",
            "prompt-hash", null, null, "v1", "v1",
            DateTimeOffset.UtcNow);

        var request = new AgentInvocationRequest(
            "inv-1", "run-1", "session-1", "agent-1",
            "fake", "model-1",
            [new RuntimeProviderMessage(RuntimeProviderRoles.User, [new TextContentBlock("hi")])],
            snapshot,
            StructuredOutput: structuredOutput);

        var adapter = new CapturingAdapter();
        var executor = new ProviderAgentInvocationExecutor(_ => adapter);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        await foreach (var _ in executor.ExecuteAsync(request, ct))
        {
        }

        Assert.IsNotNull(adapter.CapturedRequest);
        Assert.AreSame(structuredOutput, adapter.CapturedRequest.StructuredOutput);
        Assert.AreEqual(ct, adapter.CapturedRequest.CancellationToken);
    }

    private sealed class CapturingAdapter : IRuntimeProviderAdapter
    {
        public RuntimeProviderRequest? CapturedRequest { get; private set; }

        public string ProviderId => "fake";
        public ProviderCapabilities Capabilities =>
            new(Streaming: true, StructuredOutput: true);
        public IReadOnlyList<ProviderModel> Models => [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CapturedRequest = request;
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
