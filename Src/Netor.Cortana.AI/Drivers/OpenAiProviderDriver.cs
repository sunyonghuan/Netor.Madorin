using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Drivers;

public sealed class OpenAiProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("OpenAI", "OpenAI 兼容", true);
}