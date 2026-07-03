using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IPlatformMarketClient
{
    Task<ApiLoginResponse> LoginAsync(string userName, string password, CancellationToken cancellationToken = default);

    Task<ApiAccountResponse> GetMeAsync(CancellationToken cancellationToken = default);

    Task<StoreAssetPage> GetAssetsAsync(int page = 1, int pageSize = 20, AssetType? type = null, CancellationToken cancellationToken = default);

    Task<StoreAssetDetail> GetAssetDetailAsync(string assetIdOrSlug, CancellationToken cancellationToken = default);

    Task<StoreInstallManifest> GetInstallManifestAsync(string assetIdOrSlug, CancellationToken cancellationToken = default);

    Task<ApiClientSyncResponse> SyncAsync(CancellationToken cancellationToken = default);

    Task<ApiClientUpdateCheckResponse> CheckUpdatesAsync(IReadOnlyList<ApiClientInstalledAsset> installedAssets, CancellationToken cancellationToken = default);
}
