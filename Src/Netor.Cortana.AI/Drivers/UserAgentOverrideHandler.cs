using Microsoft.Extensions.Logging;

using System.Net.Http.Headers;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 替换 OpenAI SDK 默认的 User-Agent 头，避免部分中转站 Cloudflare WAF 拦截。
/// </summary>
internal sealed class UserAgentOverrideHandler(ILogger<UserAgentOverrideHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            string? requestBody = null;
            if (request.Content is not null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogWarning(
                "HTTP 请求: {Method} {Url}\nHeaders: {Headers}\nBody: {Body}",
                request.Method,
                request.RequestUri,
                request.Headers.ToString(),
                requestBody);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cortana", "1.0"));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("HTTP 请求已取消。");
            throw;
        }
    }
}