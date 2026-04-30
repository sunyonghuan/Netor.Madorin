using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

using OllamaSharp;

namespace Netor.Cortana.AI.Drivers;

public sealed class OllamaProviderDriver(IHttpClientFactory httpClientFactory) : AiProviderDriverBase
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    public override AiProviderDriverDefinition Definition { get; } =
        new("Ollama", "Ollama", true);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(provider.Url.TrimEnd('/') + "/");

        return new OllamaDelegatingChatClient(new OllamaApiClient(httpClient, model.Name, OllamaJsonSerializerContextProvider.Default));
    }

    public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
    {
        return CreateCommonOptions(agent);
    }

    public override async Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(provider.Url.TrimEnd('/') + "/");

        var client = new OllamaApiClient(httpClient, jsonSerializerContext: OllamaJsonSerializerContextProvider.Default);
        var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);

        return models.Select(model => new RemoteModelDescriptor(
            model.Name,
            model.Name,
            model.Details?.Family,
            "chat",
            null)).ToArray();
    }
}