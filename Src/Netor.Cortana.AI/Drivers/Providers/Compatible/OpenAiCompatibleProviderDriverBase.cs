using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using OpenAI;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// OpenAI 兼容协议驱动基类，复用聊天客户端和模型列表拉取逻辑。
/// </summary>
public abstract class OpenAiCompatibleProviderDriverBase(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : AiProviderDriverBase
{
    /// <summary>
    /// 用于创建 OpenAI 兼容协议请求所需的 <see cref="HttpClient"/> 实例工厂。
    /// </summary>
    protected readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    protected readonly ILoggerFactory _loggerFactory = loggerFactory;

    /// <summary>
    /// 创建指定提供商和模型对应的聊天客户端。
    /// </summary>
    /// <param name="provider">AI 提供商配置。</param>
    /// <param name="model">目标模型配置。</param>
    /// <returns>适配 <see cref="IChatClient"/> 的聊天客户端实例。</returns>
    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        // 兼容协议统一使用 API Key 鉴权，并复用命名 HttpClient 的传输配置。
        var credential = new ApiKeyCredential(provider.Key);
        var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");
        //if (provider.Url.Contains("api.deepseek.com", StringComparison.InvariantCultureIgnoreCase))
        //    httpClient = _httpClientFactory.CreateClient("Deepseek");

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

    /// <summary>
    /// 构建 OpenAI 兼容协议所需的聊天选项。
    /// </summary>
    /// <param name="provider">AI 提供商配置。</param>
    /// <param name="agent">当前智能体配置。</param>
    /// <returns>发送聊天请求时使用的选项。</returns>
    public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
    {
        return CreateOpenAiCompatibleOptions(agent);
    }

    /// <summary>
    /// 从兼容协议的模型列表端点拉取远程模型信息。
    /// </summary>
    /// <param name="provider">AI 提供商配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>远程模型描述集合。</returns>
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
                // 某些兼容服务的模型列表地址可能不同，按候选端点依次尝试。
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
                    // 响应结构不符合预期时，继续尝试下一个候选端点。
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
                        // 缺少模型标识的数据无法建立本地映射，直接跳过。
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
                // 记录最后一次异常，全部候选端点失败后统一抛出。
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("未能从任何兼容模型列表端点解析出模型数据。");
    }
}