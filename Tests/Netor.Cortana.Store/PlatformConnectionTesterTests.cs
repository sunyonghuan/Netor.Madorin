using System.Net;
using System.Security.Authentication;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.Services;

namespace Netor.Cortana.Store.Tests;

public sealed class PlatformConnectionTesterTests
{
    [Fact]
    public async Task TestAsync_WhenHealthEndpointReturnsOk_ReturnsSuccess()
    {
        var tester = new PlatformConnectionTester(new TestHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await tester.TestAsync("http://localhost:5190");

        Assert.True(result.Succeeded);
        Assert.Equal(PlatformConnectionFailureKind.None, result.FailureKind);
        Assert.Equal("连接成功，平台 API 可用。", result.Message);
    }

    [Fact]
    public async Task TestAsync_WhenUrlInvalid_ReturnsInvalidUrl()
    {
        var tester = new PlatformConnectionTester(new TestHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await tester.TestAsync("localhost:5190");

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformConnectionFailureKind.InvalidUrl, result.FailureKind);
    }

    [Fact]
    public async Task TestAsync_WhenLocalHostUnavailable_ReturnsLocalServiceUnavailable()
    {
        var tester = new PlatformConnectionTester(new TestHttpClientFactory(_ => throw new HttpRequestException("connection refused")));

        var result = await tester.TestAsync("http://localhost:5190");

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformConnectionFailureKind.LocalServiceUnavailable, result.FailureKind);
        Assert.Contains("API 已启动", result.Message);
    }

    [Fact]
    public async Task TestAsync_WhenSslFails_ReturnsSslFailure()
    {
        var tester = new PlatformConnectionTester(new TestHttpClientFactory(
            _ => throw new HttpRequestException("SSL failed", new AuthenticationException("bad certificate"))));

        var result = await tester.TestAsync("https://localhost:5190");

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformConnectionFailureKind.SslFailure, result.FailureKind);
        Assert.Contains("本地测试请使用 HTTP", result.Message);
    }

    [Fact]
    public async Task TestAsync_WhenHealthEndpointMissing_ReturnsMissingHealthEndpoint()
    {
        var tester = new PlatformConnectionTester(new TestHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await tester.TestAsync("https://platform.local");

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformConnectionFailureKind.MissingHealthEndpoint, result.FailureKind);
    }

    private sealed class TestHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new TestHandler(send));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
