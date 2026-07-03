using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;

using Netor.EventHub;

namespace Netor.Cortana.Store.Services;

public sealed class PlatformMarketClient(
    IHttpClientFactory httpClientFactory,
    IPlatformBaseUrlProvider baseUrlProvider,
    IPlatformAccountStore accountStore,
    IPublisher publisher) : IPlatformMarketClient
{
    public async Task<ApiLoginResponse> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: false, cancellationToken);
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new StoreLoginRequest(userName, password),
            StoreJsonContext.Default.StoreLoginRequest,
            cancellationToken);
        await StoreHttpResponseHelper.EnsureSuccessAsync(response, "登录平台账号", cancellationToken);

        return await response.Content.ReadFromJsonAsync(StoreJsonContext.Default.ApiLoginResponse, cancellationToken)
            ?? throw new InvalidOperationException("登录响应为空。");
    }

    public async Task<ApiAccountResponse> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: true, cancellationToken);
        return await GetAsync(client, "/api/v1/me", StoreJsonContext.Default.ApiAccountResponse, cancellationToken)
            ?? throw new InvalidOperationException("账号响应为空。");
    }

    public async Task<StoreAssetPage> GetAssetsAsync(int page = 1, int pageSize = 20, AssetType? type = null, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: false, cancellationToken);
        var query = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 100)}"
        };

        if (type is not null)
        {
            query.Add($"type={(int)type}");
        }

        var path = $"/api/v1/assets?{string.Join("&", query)}";
        return await GetAsync(client, path, StoreJsonContext.Default.StoreAssetPage, cancellationToken)
            ?? new StoreAssetPage([], page, pageSize, 0, false);
    }

    public async Task<StoreAssetDetail> GetAssetDetailAsync(string assetIdOrSlug, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: false, cancellationToken);
        return await GetAsync(client, $"/api/v1/assets/{Uri.EscapeDataString(assetIdOrSlug)}", StoreJsonContext.Default.StoreAssetDetail, cancellationToken)
            ?? throw new InvalidOperationException("资源详情为空。");
    }

    public async Task<StoreInstallManifest> GetInstallManifestAsync(string assetIdOrSlug, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: true, cancellationToken);
        return await GetAsync(client, $"/api/v1/client/install/{Uri.EscapeDataString(assetIdOrSlug)}", StoreJsonContext.Default.StoreInstallManifest, cancellationToken)
            ?? throw new InvalidOperationException("安装清单为空。");
    }

    public async Task<ApiClientSyncResponse> SyncAsync(CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: true, cancellationToken);
        return await GetAsync(client, "/api/v1/client/sync", StoreJsonContext.Default.ApiClientSyncResponse, cancellationToken)
            ?? throw new InvalidOperationException("同步响应为空。");
    }

    public async Task<ApiClientUpdateCheckResponse> CheckUpdatesAsync(
        IReadOnlyList<ApiClientInstalledAsset> installedAssets,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(requireSession: true, cancellationToken);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/updates",
            new StoreUpdateCheckRequest(installedAssets.ToList()),
            StoreJsonContext.Default.StoreUpdateCheckRequest,
            cancellationToken);

        await EnsureNotExpiredAsync(response, client.BaseAddress, cancellationToken);
        await StoreHttpResponseHelper.EnsureSuccessAsync(response, "检查资源更新", cancellationToken);
        return await response.Content.ReadFromJsonAsync(StoreJsonContext.Default.ApiClientUpdateCheckResponse, cancellationToken)
            ?? new ApiClientUpdateCheckResponse([]);
    }

    private async Task<T?> GetAsync<T>(
        HttpClient client,
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);
        await EnsureNotExpiredAsync(response, client.BaseAddress, cancellationToken);
        await StoreHttpResponseHelper.EnsureSuccessAsync(response, "请求平台接口", cancellationToken);
        return await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);
    }

    private async Task<HttpClient> CreateClientAsync(bool requireSession, CancellationToken cancellationToken)
    {
        var session = await accountStore.LoadAsync(cancellationToken);
        var baseUrl = session?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = baseUrlProvider.GetBaseUrl();
        }
        else
        {
            baseUrl = StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl);
        }

        if (requireSession && string.IsNullOrWhiteSpace(session?.AccessToken))
        {
            throw new InvalidOperationException("需要先登录平台账号。");
        }

        var client = httpClientFactory.CreateClient("CortanaStore");
        client.BaseAddress = new Uri(StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl));
        if (!string.IsNullOrWhiteSpace(session?.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        return client;
    }

    private async Task EnsureNotExpiredAsync(HttpResponseMessage response, Uri? baseAddress, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return;
        }

        await accountStore.ClearAsync(cancellationToken);
        publisher.Publish(StoreEvents.OnPlatformSessionExpired, new PlatformSessionExpiredArgs(baseAddress?.ToString() ?? string.Empty));
    }
}
