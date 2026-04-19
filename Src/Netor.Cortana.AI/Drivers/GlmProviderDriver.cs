namespace Netor.Cortana.AI.Drivers;

public sealed class GlmProviderDriver(IHttpClientFactory httpClientFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("GLM", "GLM / 智谱", true);
}