namespace Cortana.Plugins.Office;

/// <summary>
/// 办公插件配置选项。
/// </summary>
public sealed class OfficePluginOptions
{
    /// <summary>
    /// 允许访问的目录白名单。为空数组时不限制目录访问（开发模式）。
    /// </summary>
    public string[] AllowedDirectories { get; set; } = [];
}
