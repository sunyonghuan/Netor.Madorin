using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IAssetInstallTargetResolver
{
    string ResolveRootDirectory(AssetType assetType);
}
