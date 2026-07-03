using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

using OpenAI;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// Gemini 驱动 —— 通过 OpenAI 兼容协议与 Gemini API 通信，避免原生 SDK 的 AOT 不兼容问题。
/// <para>
/// 官方端点自动指向 <c>https://generativelanguage.googleapis.com/v1beta/openai</c>；
/// 若配置了自定义 URL（中转站），则直接使用该 URL。
/// </para>
/// </summary>
public sealed class GeminiProviderDriver(IHttpClientFactory httpClientFactory) : AiProviderDriverBase
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    public override AiProviderDriverDefinition Definition { get; } =
        new("Gemini", "Gemini", true);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var credential = new ApiKeyCredential(provider.Key);
        var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");
        var baseUrl = ResolveBaseUrl(provider);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
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
        var baseUrl = ResolveBaseUrl(provider);
        Exception? lastException = null;

        foreach (var requestUrl in BuildModelEndpointCandidates(baseUrl))
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
                        item.TryGetProperty("owned_by", out var ownedByProperty)
                            ? ownedByProperty.GetString()
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

        throw lastException ?? new InvalidOperationException("未能从任何兼容模型列表端点解析出模型数据。");
    }

    private static string ResolveBaseUrl(AiProviderEntity provider)
        => string.IsNullOrWhiteSpace(provider.Url) ? DefaultBaseUrl : provider.Url.TrimEnd('/');
}