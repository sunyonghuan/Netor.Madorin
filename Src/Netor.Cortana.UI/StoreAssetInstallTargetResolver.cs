using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;

namespace Netor.Cortana.UI;

internal sealed class StoreAssetInstallTargetResolver(IAppPaths appPaths) : IAssetInstallTargetResolver
{
    public string ResolveRootDirectory(AssetType assetType)
        => assetType switch
        {
            AssetType.Plugin => appPaths.UserPluginsDirectory,
            AssetType.Skill => appPaths.UserSkillsDirectory,
            AssetType.Agent => appPaths.UserAgentsDirectory,
            AssetType.Solution => appPaths.UserSolutionsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "未知资源类型。")
        };
}
