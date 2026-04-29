using System.Net;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// OpenAI 兼容端点处理器：/v1/chat/completions。
/// 采用原始 JSON 透传，保留 tools/tool_calls/reasoning 等完整协议字段。
/// </summary>
public sealed class OpenAiCompatibleEndpoints(OpenAiCompatibleRawProxy rawProxy)
{
    public async Task HandleChatCompletionsAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await rawProxy.ForwardChatCompletionsAsync(
                context.Request.InputStream,
                ProxyModelNameMapper.ToInternalModelName,
                cancellationToken);

            await OllamaHttpResponseWriter.WriteRawAsync(
                context.Response,
                result.StatusCode,
                result.ContentType,
                result.Body,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await OllamaHttpResponseWriter.WriteErrorAsync(context.Response, 500, ex.Message, cancellationToken);
        }
    }
}
