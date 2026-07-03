using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.Services;
using Netor.EventHub;

namespace Netor.Cortana.Store.Tests;

public sealed class PlatformMarketClientTests
{
    [Fact]
    public async Task LoginAsync_UsesNormalizedHttpsBaseUrl()
    {
        var httpClientFactory = new CapturingHttpClientFactory(
            new ApiLoginResponse(
                "token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ApiAccountResponse("account-1", 1, "tester", "测试用户", "tester@example.com", "13800000000")));
        var accountStore = new TestAccountStore();

        var services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        var client = new PlatformMarketClient(
            httpClientFactory,
            new TestBaseUrlProvider("http://api.madorin.netor.me"),
            accountStore,
            services.GetRequiredService<IPublisher>());

        await client.LoginAsync("tester", "123456");

        Assert.Equal(HttpMethod.Post, httpClientFactory.LastMethod);
        Assert.Equal(new Uri("https://api.madorin.netor.me/api/v1/auth/login"), httpClientFactory.LastRequestUri);
    }

    [Fact]
    public async Task GetAssetsAsync_SendsPageAndPageSize()
    {
        var httpClientFactory = new CapturingHttpClientFactory(
            new StoreAssetPage([], 2, 20, 55, true));
        var accountStore = new TestAccountStore();

        var services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        var client = new PlatformMarketClient(
            httpClientFactory,
            new TestBaseUrlProvider("https://platform.local"),
            accountStore,
            services.GetRequiredService<IPublisher>());

        await client.GetAssetsAsync(page: 2, pageSize: 20);

        Assert.Equal(HttpMethod.Get, httpClientFactory.LastMethod);
        Assert.Equal(new Uri("https://platform.local/api/v1/assets?page=2&pageSize=20"), httpClientFactory.LastRequestUri);
    }

    [Fact]
    public async Task CheckUpdatesAsync_SendsCamelCaseInstalledAssetsAndBearerToken()
    {
        var httpClientFactory = new CapturingHttpClientFactory(new ApiClientUpdateCheckResponse([]));
        var accountStore = new TestAccountStore
        {
            Session = new PlatformAccountSession(
                "https://platform.local",
                "token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ApiAccountResponse("account-1", 1, "tester", "测试用户", "tester@example.com", "13800000000"))
        };

        var services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        var client = new PlatformMarketClient(
            httpClientFactory,
            new TestBaseUrlProvider(),
            accountStore,
            services.GetRequiredService<IPublisher>());

        await client.CheckUpdatesAsync(
        [
            new ApiClientInstalledAsset("asset-1", "demo-plugin", "version-1", "1.0.0", "hash")
        ]);

        Assert.Equal(HttpMethod.Post, httpClientFactory.LastMethod);
        Assert.Equal(new Uri("https://platform.local/api/v1/client/updates"), httpClientFactory.LastRequestUri);
        Assert.Equal("Bearer", httpClientFactory.LastAuthorizationScheme);
        Assert.Equal("token", httpClientFactory.LastAuthorizationParameter);
        Assert.DoesNotContain("InstalledAssets", httpClientFactory.LastRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetId", httpClientFactory.LastRequestBody, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(httpClientFactory.LastRequestBody);
        var installedAssets = document.RootElement.GetProperty("installedAssets");
        var installed = installedAssets.EnumerateArray().Single();
        Assert.Equal("asset-1", installed.GetProperty("assetId").GetString());
        Assert.Equal("demo-plugin", installed.GetProperty("assetSlug").GetString());
        Assert.Equal("version-1", installed.GetProperty("versionId").GetString());
        Assert.Equal("1.0.0", installed.GetProperty("versionName").GetString());
        Assert.Equal("hash", installed.GetProperty("packageHash").GetString());
    }

    private sealed class CapturingHttpClientFactory : IHttpClientFactory
    {
        private readonly object _responsePayload;

        public CapturingHttpClientFactory(object responsePayload)
        {
            _responsePayload = responsePayload;
        }

        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        public HttpClient CreateClient(string name)
            => new(new Handler(this));

        private sealed class Handler(CapturingHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.LastRequestUri = request.RequestUri;
                owner.LastMethod = request.Method;
                owner.LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
                owner.LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
                owner.LastRequestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(owner._responsePayload)
                };
            }
        }
    }

    private sealed class TestBaseUrlProvider(string baseUrl = "https://platform.local") : IPlatformBaseUrlProvider
    {
        public string GetBaseUrl() => baseUrl;
    }

    private sealed class TestAccountStore : IPlatformAccountStore
    {
        public PlatformAccountSession? Session { get; set; }

        public Task<PlatformAccountSession?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Session);

        public Task SaveAsync(PlatformAccountSession session, CancellationToken cancellationToken = default)
        {
            Session = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Session = null;
            return Task.CompletedTask;
        }
    }
}
