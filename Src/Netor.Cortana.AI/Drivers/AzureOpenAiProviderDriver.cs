using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

public sealed class AzureOpenAiProviderDriver(IHttpClientFactory httpClientFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Azure", "Azure OpenAI", false);

    public override Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RemoteModelDescriptor>>([]);
    }
}