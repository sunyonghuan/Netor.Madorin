using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IInstalledAssetStore
{
    Task<IReadOnlyList<InstalledAssetRecord>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<InstalledAssetRecord> assets, CancellationToken cancellationToken = default);

    Task UpsertAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default);

    Task RemoveAsync(string assetSlug, CancellationToken cancellationToken = default);
}
