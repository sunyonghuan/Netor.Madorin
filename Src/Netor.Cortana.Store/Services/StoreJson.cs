using System.Text.Json;
using System.Text.Json.Serialization;

using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Services;

internal static class StoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StoreLoginRequest))]
[JsonSerializable(typeof(StoreUpdateCheckRequest))]
[JsonSerializable(typeof(PlatformAccountSession))]
[JsonSerializable(typeof(ApiLoginResponse))]
[JsonSerializable(typeof(ApiAccountResponse))]
[JsonSerializable(typeof(List<StoreAssetItem>))]
[JsonSerializable(typeof(StoreAssetPage))]
[JsonSerializable(typeof(StoreAssetDetail))]
[JsonSerializable(typeof(StoreAssetVersion))]
[JsonSerializable(typeof(StorePricingPlan))]
[JsonSerializable(typeof(StoreInstallManifest))]
[JsonSerializable(typeof(StoreInstallVersion))]
[JsonSerializable(typeof(ApiClientSyncResponse))]
[JsonSerializable(typeof(ApiClientUpdateCheckResponse))]
[JsonSerializable(typeof(ApiClientUpdateItem))]
[JsonSerializable(typeof(List<ApiClientInstalledAsset>))]
[JsonSerializable(typeof(InstalledAssetFile))]
[JsonSerializable(typeof(InstalledAssetRecord))]
[JsonSerializable(typeof(SolutionPackageManifest))]
[JsonSerializable(typeof(SolutionPackageAssets))]
[JsonSerializable(typeof(SolutionPackageAsset))]
[JsonSerializable(typeof(SolutionInstalledAssetsFile))]
[JsonSerializable(typeof(SolutionInstalledAssetRecord))]
[JsonSerializable(typeof(SolutionAssetOperationRecord))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

internal sealed record StoreLoginRequest(string UserName, string Password);

internal sealed record StoreUpdateCheckRequest(List<ApiClientInstalledAsset> InstalledAssets);
