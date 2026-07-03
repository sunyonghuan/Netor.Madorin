using System.Net;
using System.Security.Authentication;

using Netor.Cortana.Entitys;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Services;

public sealed class PlatformConnectionTester(IHttpClientFactory httpClientFactory) : IPlatformConnectionTester
{
    public async Task<PlatformConnectionTestResult> TestAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        baseUrl = StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new PlatformConnectionTestResult(
                false,
                "平台地址格式无效，请输入 http:// 或 https:// 开头的完整地址。",
                PlatformConnectionFailureKind.InvalidUrl);
        }

        try
        {
            using var client = httpClientFactory.CreateClient("CortanaStore");
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync(BuildUri(uri, "/api/health"), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new PlatformConnectionTestResult(true, "连接成功，平台 API 可用。");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new PlatformConnectionTestResult(
                    false,
                    "平台服务可达，但未发现健康检查接口。请确认地址指向 Netor.Cortana.Platform.Api。",
                    PlatformConnectionFailureKind.MissingHealthEndpoint);
            }

            return new PlatformConnectionTestResult(
                false,
                $"平台服务返回 HTTP {(int)response.StatusCode}，请确认 API 状态。",
                PlatformConnectionFailureKind.UnexpectedStatusCode);
        }
        catch (TaskCanceledException)
        {
            return StorePlatformUrlHelper.IsLocalHost(uri)
                ? new PlatformConnectionTestResult(
                    false,
                    "无法连接平台服务，请确认本地 API 已启动。",
                    PlatformConnectionFailureKind.LocalServiceUnavailable)
                : new PlatformConnectionTestResult(
                    false,
                    "平台地址连接超时，请检查 Platform.BaseUrl 或网络状态。",
                    PlatformConnectionFailureKind.Unreachable);
        }
        catch (HttpRequestException ex) when (IsSslFailure(ex))
        {
            return new PlatformConnectionTestResult(
                false,
                $"SSL 连接失败。本地测试请使用 HTTP 地址，例如 {AppBranding.LocalPlatformApiBaseUrl}。",
                PlatformConnectionFailureKind.SslFailure);
        }
        catch (HttpRequestException)
        {
            return StorePlatformUrlHelper.IsLocalHost(uri)
                ? new PlatformConnectionTestResult(
                    false,
                    "无法连接平台服务，请确认 API 已启动并监听当前端口。",
                    PlatformConnectionFailureKind.LocalServiceUnavailable)
                : new PlatformConnectionTestResult(
                    false,
                    "平台地址不可达，请检查 Platform.BaseUrl。",
                    PlatformConnectionFailureKind.Unreachable);
        }
    }

    private static Uri BuildUri(Uri baseUri, string path)
        => new($"{baseUri.GetLeftPart(UriPartial.Authority)}{path}");
    private static bool IsSslFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            if (current.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
