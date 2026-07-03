using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.ModelCapability;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus model topic 上的模型能力请求与错误响应。
/// </summary>
internal sealed class PluginBusModelCapabilityDispatcher(
    IPluginModelCapabilityService modelCapabilityService,
    ILogger logger,
    Func<string, string, CancellationToken, Task> sendAsync)
{
    /// <summary>
    /// 处理模型能力调用请求，将请求转发给模型能力服务并返回统一响应。
    /// </summary>
    /// <param name="clientId">模型能力客户端连接标识。</param>
    /// <param name="json">客户端发送的请求 JSON。</param>
    /// <param name="cancellationToken">取消处理和响应发送的令牌。</param>
    public async Task HandleAsync(string clientId, string json, CancellationToken cancellationToken)
    {
        ModelCapabilityRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.ModelCapabilityRequest);
            if (request is null)
            {
                await SendErrorAsync(clientId, null, "INVALID_REQUEST", "请求内容为空。", false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(request.Topic, CortanaWsEndpoints.ModelTopic, StringComparison.Ordinal)
                || !string.Equals(request.Op, CortanaWsEndpoints.ModelCapabilityRequestOperation, StringComparison.Ordinal))
            {
                await SendErrorAsync(clientId, request, "INVALID_OPERATION", "模型能力请求 topic/op 不匹配。", false, cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = await modelCapabilityService.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(response, WebSocketJsonContext.Default.ModelCapabilityResponse);
            await sendAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            await SendErrorAsync(clientId, request, "UNAUTHORIZED_CAPABILITY", ex.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await SendErrorAsync(clientId, request, "TIMEOUT", ex.Message, true, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(clientId, request, "MODEL_NOT_CONFIGURED", ex.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 model-capability 请求失败：{Json}", json);
            await SendErrorAsync(clientId, request, "INTERNAL_ERROR", ex.Message, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendErrorAsync(
        string clientId,
        ModelCapabilityRequest? request,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ModelCapabilityResponse
        {
            RequestId = request?.RequestId ?? string.Empty,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        }, WebSocketJsonContext.Default.ModelCapabilityResponse);

        return sendAsync(clientId, payload, cancellationToken);
    }
}
