using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Drivers;

public sealed class GlmProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("GLM", "GLM / 智谱", true);
}