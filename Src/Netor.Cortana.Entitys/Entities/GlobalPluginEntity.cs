namespace Netor.Cortana.Entitys;

/// <summary>
/// 全局插件启用配置。
/// </summary>
public sealed class GlobalPluginEntity : BaseEntity
{
    /// <summary>
    /// 插件 ID。
    /// </summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>
    /// 是否作为全局插件启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}