namespace Netor.Cortana.Entitys;

/// <summary>
/// 应用品牌默认值，作为多品牌发布脚本的第一阶段收口入口。
/// </summary>
public static class AppBranding
{
    /// <summary>
    /// 显示在窗口、托盘、关于页等用户可见位置的应用名称。
    /// </summary>
    public const string DisplayName = "玛得令";

    /// <summary>
    /// 关于页和托盘等位置展示的应用副标题。
    /// </summary>
    public const string AssistantSubtitle = "Madorin AI 助手";

    /// <summary>
    /// Avalonia 资源所在的程序集名称，用于拼接 avares 资源地址。
    /// </summary>
    public const string ResourceAssemblyName = "Madorin";

    /// <summary>
    /// 桌面主程序的程序集名称，不包含扩展名。
    /// </summary>
    public const string DesktopAssemblyName = "Madorin";

    /// <summary>
    /// Windows 桌面主程序发布后的可执行文件名。
    /// </summary>
    public const string DesktopExecutableFileName = DesktopAssemblyName + ".exe";

    /// <summary>
    /// 原生插件宿主进程的程序集名称。
    /// </summary>
    public const string NativeHostAssemblyName = "Madorin.NativeHost";

    /// <summary>
    /// Windows 原生插件宿主发布后的可执行文件名。
    /// </summary>
    public const string NativeHostExecutableFileName = NativeHostAssemblyName + ".exe";

    /// <summary>
    /// 默认发布目录名称。
    /// </summary>
    public const string ReleaseDirectoryName = "Madorin";

    /// <summary>
    /// 默认安装包名称。
    /// </summary>
    public const string PackageName = "Netor.Cortana";

    /// <summary>
    /// 应用图标文件路径。
    /// </summary>
    public const string ApplicationIconPath = @"Assets\brand.ico";

    /// <summary>
    /// 应用位图 Logo 文件名。
    /// </summary>
    public const string LogoImageFileName = "brand.png";

    /// <summary>
    /// 品牌官方网站 Host。
    /// </summary>
    public const string WebsiteHost = "madorin.netor.me";

    /// <summary>
    /// 品牌官方网站默认 HTTPS 地址。
    /// </summary>
    public const string WebsiteBaseUrl = "https://" + WebsiteHost;

    /// <summary>
    /// 系统设置表中保存平台 API 地址的键名。
    /// </summary>
    public const string PlatformBaseUrlSettingKey = "Platform.BaseUrl";

    /// <summary>
    /// 本地开发环境的平台 API 默认地址。
    /// </summary>
    public const string LocalPlatformApiBaseUrl = "http://localhost:5190";

    /// <summary>
    /// 线上运营平台的 API 默认地址。
    /// </summary>
    public const string ProductionPlatformApiBaseUrl = "https://platform.netor.me";

    /// <summary>
    /// 旧版本使用过的线上平台地址，用于升级时迁移历史默认值。
    /// </summary>
    public const string LegacyProductionPlatformApiBaseUrl = ProductionPlatformApiBaseUrl;

    /// <summary>
    /// 平台 API 地址设置项在系统设置页面展示的说明文本。
    /// </summary>
    public const string PlatformBaseUrlDescription =
        "应用商店连接的运营平台 API 地址。本地联调使用 " + LocalPlatformApiBaseUrl + "；切换后需要重新登录平台账号。";

    /// <summary>
    /// 根据品牌资源程序集名称生成 Avalonia 资源地址。
    /// </summary>
    /// <param name="assetPath">Assets 目录下的资源相对路径，或包含 Assets 前缀的项目资源路径。</param>
    /// <returns>可供 Avalonia AssetLoader 使用的 avares 资源地址。</returns>
    public static string AssetUri(string assetPath)
    {
        var normalizedPath = assetPath.Replace('\\', '/').TrimStart('/');
        const string assetsPrefix = "Assets/";
        if (normalizedPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[assetsPrefix.Length..];
        }

        return $"avares://{ResourceAssemblyName}/Assets/{normalizedPath}";
    }
}




