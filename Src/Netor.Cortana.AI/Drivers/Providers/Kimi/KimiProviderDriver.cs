using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using OpenAI;

using System.ClientModel;
using System.ClientModel.Primitives;

namespace Netor.Cortana.AI.Drivers;

public sealed class KimiProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Kimi", "Kimi 专用协议", true, DefaultMaxTools: 128);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var credential = new ApiKeyCredential(provider.Key);
        var httpClient = _httpClientFactory.CreateClient("Kimi");
        var options = new OpenAIClientOptions
        {
            // 基址由具体提供商决定，这里仅做协议层适配。
            Endpoint = new Uri(provider.Url.TrimEnd('/')),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(httpClient),
            ClientLoggingOptions = new ClientLoggingOptions()
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024,
                LoggerFactory = _loggerFactory
            }
        };

        return new OpenAIClient(credential, options)
            .GetChatClient(model.Name)
            .AsIChatClient();
    }
}
