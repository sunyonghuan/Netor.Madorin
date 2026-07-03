using Microsoft.Extensions.Logging;

using System.Net.Http.Headers;
using System.Text;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 替换 OpenAI SDK 默认的 User-Agent 头，避免部分中转站 Cloudflare WAF 拦截。
/// </summary>
internal sealed class UserAgentOverrideHandler(
    ILogger<UserAgentOverrideHandler> logger,
    SystemSettingsService systemSettings) : DelegatingHandler
{
    private const string TraceEnabledEnvironmentVariable = "CORTANA_AI_TRACE_ENABLED";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var traceEnabled = ResolveTraceEnabled(systemSettings);
            string? requestBody = null;
            if (traceEnabled && request.Content is not null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cortana", "1.0"));

            if (traceEnabled)
            {
                logger.LogWarning(
                    "HTTP 请求: {Method} {Url}\nHeaders: {Headers}\nBody: {Body}",
                    request.Method,
                    request.RequestUri,
                    RedactHeaders(request.Headers),
                    requestBody);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (traceEnabled)
            {
                string? responseBody = null;
                if (response.Content is not null)
                {
                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                logger.LogWarning(
                    "HTTP 响应: {Method} {Url} => {StatusCode}\nHeaders: {Headers}\nBody: {Body}",
                    request.Method,
                    request.RequestUri,
                    (int)response.StatusCode,
                    response.Headers.ToString(),
                    responseBody);

                if (response.Content is not null && responseBody is not null)
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
                    response.Content = new StringContent(responseBody, Encoding.UTF8, mediaType);
                }
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("HTTP 请求已取消。");
            throw;
        }
    }

    private static bool ResolveTraceEnabled(SystemSettingsService systemSettings)
    {
        var environmentValue = Environment.GetEnvironmentVariable(TraceEnabledEnvironmentVariable);
        if (bool.TryParse(environmentValue, out var enabledFromEnvironment))
        {
            return enabledFromEnvironment;
        }

        if (systemSettings is not null)
        {
            return systemSettings.GetValue("AI.Trace.Enabled", false);
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static string RedactHeaders(HttpRequestHeaders headers)
    {
        var text = headers.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            "Authorization:\\s*Bearer\\s+.+",
            "Authorization: Bearer ***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}