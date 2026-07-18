namespace Netor.Cortana.Entitys;

/// <summary>Application brand defaults for branded publish.</summary>
public static class AppBranding
{
    /// <summary>Application display name.</summary>
    public const string DisplayName = "马得令";

    /// <summary>Application subtitle.</summary>
    public const string AssistantSubtitle = "Madorin AI 助手";

    /// <summary>Avalonia resource assembly name.</summary>
    public const string ResourceAssemblyName = "Madorin";

    /// <summary>Desktop assembly name without extension.</summary>
    public const string DesktopAssemblyName = "Madorin";

    /// <summary>Windows desktop executable file name.</summary>
    public const string DesktopExecutableFileName = DesktopAssemblyName + ".exe";

    /// <summary>Native plugin host assembly name.</summary>
    public const string NativeHostAssemblyName = "Madorin.NativeHost";

    /// <summary>Windows native plugin host executable file name.</summary>
    public const string NativeHostExecutableFileName = NativeHostAssemblyName + ".exe";

    /// <summary>Default release directory name.</summary>
    public const string ReleaseDirectoryName = "Madorin";

    /// <summary>Default package name.</summary>
    public const string PackageName = "Madorin";

    /// <summary>Application icon path.</summary>
    public const string ApplicationIconPath = "Assets\\brand.ico";

    /// <summary>Application bitmap logo file name.</summary>
    public const string LogoImageFileName = "brand.png";

    /// <summary>Brand website host.</summary>
    public const string WebsiteHost = "madorin.netor.me";

    /// <summary>Brand website HTTPS base URL.</summary>
    public const string WebsiteBaseUrl = "https://" + WebsiteHost;

    /// <summary>System setting key for the platform API address.</summary>
    public const string PlatformBaseUrlSettingKey = "Platform.BaseUrl";

    /// <summary>Local development platform API base URL.</summary>
    public const string LocalPlatformApiBaseUrl = "http://localhost:5190";

    /// <summary>Production platform API base URL.</summary>
    public const string ProductionPlatformApiBaseUrl = "https://platform.netor.me";

    /// <summary>Legacy production platform API base URL.</summary>
    public const string LegacyProductionPlatformApiBaseUrl = ProductionPlatformApiBaseUrl;

    /// <summary>Description shown for the platform API base URL setting.</summary>
    public const string PlatformBaseUrlDescription = "应用商店连接的运营平台 API 地址。本地联调使用 http://localhost:5190；切换后需要重新登录平台账号。";

    /// <summary>Builds an Avalonia asset URI from the brand resource assembly.</summary>
    public static string AssetUri(string assetPath)
    {
        var normalizedPath = assetPath.Replace('\\', '/').TrimStart('/');
        const string assetsPrefix = "Assets/";
        if (normalizedPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[assetsPrefix.Length..];
        }

        return "avares://" + ResourceAssemblyName + "/Assets/" + normalizedPath;
    }
}
