using System.Text.Json;
using Netor.Cortana.Entitys;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Services;

public sealed class InstalledAssetStore(IAppPaths appPaths) : IInstalledAssetStore
{
    private string FilePath => Path.Combine(appPaths.UserDataDirectory, "store", "installed-assets.json");

    public async Task<IReadOnlyList<InstalledAssetRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync(stream, StoreJsonContext.Default.InstalledAssetFile, cancellationToken);
            return file?.Assets ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<InstalledAssetRecord> assets, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, new InstalledAssetFile(assets), StoreJsonContext.Default.InstalledAssetFile, cancellationToken);
    }

    public async Task UpsertAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default)
    {
        var assets = (await LoadAsync(cancellationToken)).ToList();
        var index = assets.FindIndex(x => string.Equals(x.AssetSlug, asset.AssetSlug, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            assets[index] = asset;
        }
        else
        {
            assets.Add(asset);
        }

        await SaveAsync(assets, cancellationToken);
    }

    public async Task RemoveAsync(string assetSlug, CancellationToken cancellationToken = default)
    {
        var assets = (await LoadAsync(cancellationToken))
            .Where(x => !string.Equals(x.AssetSlug, assetSlug, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await SaveAsync(assets, cancellationToken);
    }
}
