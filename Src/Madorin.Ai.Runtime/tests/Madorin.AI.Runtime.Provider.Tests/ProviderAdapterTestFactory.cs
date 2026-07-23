using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

public enum ProviderAdapterKind
{
    OpenAI,
    Anthropic,
    OpenAICompatible
}

internal sealed class ProviderAdapterFixture : IDisposable
{
    public ProviderAdapterFixture(IRuntimeProviderAdapter adapter)
    {
        Adapter = adapter;
    }

    public IRuntimeProviderAdapter Adapter { get; }

    public void Dispose()
    {
        (Adapter as IDisposable)?.Dispose();
    }
}

internal static class ProviderAdapterTestFactory
{
    public const string ApiKey = "provider-test-key";
    public const string ModelId = "test-model";

    public static ProviderAdapterFixture Create(
        ProviderAdapterKind kind,
        Uri baseAddress,
        ProviderCapabilities? modelCapabilities = null,
        IProviderBlobResolver? blobResolver = null)
    {
        var capabilities = modelCapabilities ?? CreateCapabilities();
        ProviderModel[] models = [new(ModelId, "Test model", Capabilities: capabilities)];
        var root = baseAddress.AbsoluteUri.TrimEnd('/');

        IRuntimeProviderAdapter adapter = kind switch
        {
            ProviderAdapterKind.OpenAI => new OpenAIProviderAdapter(
                ApiKey,
                $"{root}/v1",
                models,
                blobResolver),
            ProviderAdapterKind.Anthropic => new AnthropicProviderAdapter(
                ApiKey,
                root,
                models,
                blobResolver),
            ProviderAdapterKind.OpenAICompatible => new OpenAICompatibleProviderAdapter(
                new OpenAICompatibleProviderOptions(
                    "compatible",
                    $"{root}/v1",
                    ApiKey,
                    Capabilities: CreateCapabilities(),
                    Models: models),
                new HttpClient(),
                disposeHttpClient: true,
                blobResolver),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return new ProviderAdapterFixture(adapter);
    }

    public static RuntimeProviderRequest CreateRequest(
        ToolDescriptor[]? tools = null,
        RuntimeStructuredOutput? structuredOutput = null,
        CancellationToken requestCancellation = default) =>
        new(
            "inv-1",
            "agent-1",
            "test-provider",
            ModelId,
            [new RuntimeProviderMessage(RuntimeProviderRoles.User, [new TextContentBlock("sensitive prompt")])],
            tools,
            Temperature: 0.25f,
            MaxTokens: 128,
            StructuredOutput: structuredOutput,
            CancellationToken: requestCancellation);

    public static async Task<List<RuntimeProviderEvent>> CollectAsync(
        IRuntimeProviderAdapter adapter,
        RuntimeProviderRequest request,
        CancellationToken ct = default)
    {
        var events = new List<RuntimeProviderEvent>();
        await foreach (var providerEvent in adapter.CompleteStreamingAsync(request, ct)
                           .WithCancellation(ct))
        {
            events.Add(providerEvent);
        }

        return events;
    }

    public static ProviderCapabilities CreateCapabilities(
        bool toolCalling = true,
        bool structuredOutput = true,
        bool reasoning = true,
        bool vision = true,
        bool files = true,
        bool usage = true) =>
        new(
            Streaming: true,
            ToolCalling: toolCalling,
            Vision: vision,
            StructuredOutput: structuredOutput,
            Reasoning: reasoning,
            Files: files,
            Usage: usage,
            RemoteCancellation: false);
}
