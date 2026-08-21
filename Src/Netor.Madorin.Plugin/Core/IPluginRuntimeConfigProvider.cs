namespace Netor.Madorin.Plugin;

/// <summary>
/// 为插件 init 阶段生成宿主私有运行时配置。
/// </summary>
public interface IPluginRuntimeConfigProvider
{
    /// <summary>
    /// 根据插件配置声明和已保存设置构建插件有效配置 JSON。
    /// </summary>
    string BuildPluginConfigJson(string pluginId, PluginManifest manifest);

    /// <summary>
    /// 根据插件宿主能力申请和授权设置构建运行时授权摘要 JSON。
    /// </summary>
    string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest);
}
