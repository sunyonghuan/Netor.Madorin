using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IPackageInstallService
{
    Task CleanupStaleStagingAsync(CancellationToken cancellationToken = default);

    Task<PackageInstallResult> InstallAsync(StoreInstallManifest manifest, CancellationToken cancellationToken = default);

    Task<PackageInstallResult> UninstallAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default);
}
