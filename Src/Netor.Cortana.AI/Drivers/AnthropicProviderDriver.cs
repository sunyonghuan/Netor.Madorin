using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

using OpenAI;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// Anthropic 驱动 —— 通过 OpenAI 兼容协议与 Anthropic API 代理通信，避免原生 SDK 的 AOT 不兼容问题。
/// <para>
/// 官方 Anthropic API 不支持 OpenAI 兼容协议，需使用支持该协议的代理服务（中转站）。
/// </para>
/// </summary>
public sealed class AnthropicProviderDriver(IHttpClientFactory httpClientFactory) : AiProviderDriverBase
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    public override AiProviderDriverDefinition Definition { get; } =
        new("Anthropic", "Anthropic", true);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(provider.Url))
        {
            throw new InvalidOperationException(
                "Anthropic 驱动需要配置代理 URL（官方 API 不支持 OpenAI 兼容协议）。");
        }

        var credential = new ApiKeyCredential(provider.Key);
        var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Url.TrimEnd('/')),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(httpClient)
        };

        return new OpenAIClient(credential, options)
            .GetChatClient(model.Name)
            .AsIChatClient();
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
        Exception? lastException = null;

        foreach (var requestUrl in BuildModelEndpointCandidates(provider.Url))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Add("Authorization", $"Bearer {provider.Key}");

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (!document.RootElement.TryGetProperty("data", out var dataArray)
                    || dataArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var models = new List<RemoteModelDescriptor>();
                foreach (var item in dataArray.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProperty)
                        ? idProperty.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    models.Add(new RemoteModelDescriptor(
                        id,
                        item.TryGetProperty("display_name", out var displayNameProperty)
                            ? displayNameProperty.GetString()
                            : null,
                        item.TryGetProperty("type", out var typeProperty)
                            ? typeProperty.GetString()
                            : null,
                        "chat",
                        null));
                }

                return models;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("未能从任何 Anthropic 模型列表端点解析出模型数据。");
    }
}