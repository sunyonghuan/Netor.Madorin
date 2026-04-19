namespace Netor.Cortana.AI.Drivers;

public sealed class OpenAiProviderDriver(IHttpClientFactory httpClientFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("OpenAI", "OpenAI 兼容", true);
}