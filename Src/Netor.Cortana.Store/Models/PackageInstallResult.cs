namespace Netor.Cortana.Store.Models;

public sealed record PackageInstallResult(bool Succeeded, string Message, InstalledAssetRecord? InstalledAsset)
{
    public static PackageInstallResult Success(InstalledAssetRecord asset)
        => new(true, "安装完成。", asset);

    public static PackageInstallResult Uninstalled(InstalledAssetRecord asset)
        => new(true, "卸载完成。", asset);

    public static PackageInstallResult Failed(string message)
        => new(false, message, null);
}
