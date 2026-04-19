using System.Net.Http.Headers;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 替换 OpenAI SDK 默认的 User-Agent 头，避免部分中转站 Cloudflare WAF 拦截。
/// </summary>
internal sealed class UserAgentOverrideHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cortana", "1.0"));
        return base.SendAsync(request, cancellationToken);
    }
}
