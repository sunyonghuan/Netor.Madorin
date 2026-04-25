using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using OpenAI;

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text;

namespace Netor.Cortana.AI.Drivers;

public sealed class DeepseekProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
: OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Deepseek", "Deepseek 专用协议", true);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var credential = new ApiKeyCredential(provider.Key);
        var httpClient = _httpClientFactory.CreateClient("Deepseek");
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

        var innerClient = new OpenAIClient(credential, options)
            .GetChatClient(model.Name)
            .AsIChatClient();

        return new DeepseekDelegatingChatClient(innerClient);
    }
}