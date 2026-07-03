using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Drivers;

public sealed class CustomProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Custom", "自定义", true);
}