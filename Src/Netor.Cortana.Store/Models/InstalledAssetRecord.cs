namespace Netor.Cortana.Store.Models;

public sealed record InstalledAssetRecord(
    string AssetId,
    string AssetSlug,
    AssetType AssetType,
    string AssetName,
    string VersionId,
    string VersionName,
    string PackageHash,
    long PackageSize,
    string InstalledDirectory,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset LastVerifiedAtUtc);

public sealed record InstalledAssetFile(IReadOnlyList<InstalledAssetRecord> Assets);

public sealed record ApiClientInstalledAsset(
    string? AssetId,
    string? AssetSlug,
    string? VersionId,
    string? VersionName,
    string? PackageHash);

public sealed record ApiClientSyncResponse(ApiAccountResponse Account, IReadOnlyList<StoreInstallManifest> Assets);

public sealed record ApiClientUpdateCheckResponse(IReadOnlyList<ApiClientUpdateItem> Updates);

public sealed record ApiClientUpdateItem(
    string? InstalledAssetId,
    string? InstalledAssetSlug,
    string? InstalledVersionId,
    string? InstalledVersionName,
    StoreInstallManifest Current);
