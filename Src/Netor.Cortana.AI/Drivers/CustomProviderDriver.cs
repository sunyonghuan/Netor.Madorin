namespace Netor.Cortana.AI.Drivers;

public sealed class CustomProviderDriver(IHttpClientFactory httpClientFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Custom", "自定义", true);
}