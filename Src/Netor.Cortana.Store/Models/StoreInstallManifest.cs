using System.Text.Json;

namespace Netor.Cortana.Store.Models;

public sealed record StoreInstallManifest(
    string AssetId,
    string AssetSlug,
    AssetType AssetType,
    string AssetName,
    string DeveloperName,
    StoreInstallVersion Version,
    string DownloadUrl,
    JsonElement? Manifest);

public sealed record StoreInstallVersion(
    string VersionId,
    string VersionName,
    string ReleaseNotes,
    string PackageHash,
    string HashAlgorithm,
    long PackageSize);
