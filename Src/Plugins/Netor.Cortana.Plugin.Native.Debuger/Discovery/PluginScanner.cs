using System.Reflection;

namespace Netor.Cortana.Plugin.Native.Debugger.Discovery;

/// <summary>
/// 插件元数据信息
/// </summary>
public record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string Description,
    string[] Tags,
    string? Instructions,
    Type PluginType
);

/// <summary>
/// 插件扫描器 - 确保每个插件项目有且仅有一个 [Plugin] 标记的入口类
/// </summary>
public static class PluginScanner
{
    /// <summary>
    /// 扫描程序集，查找并验证 [Plugin] 标记的入口类
    /// </summary>
    /// <param name="assembly">要扫描的程序集</param>
    /// <returns>插件元数据</returns>
    /// <exception cref="InvalidOperationException">当未找到或找到多个 [Plugin] 标记类时抛出</exception>
    public static PluginMetadata Scan(Assembly assembly)
    {
        var plugin = TryScan(assembly);
        if (plugin == null)
        {
            throw new InvalidOperationException(
                $"程序集 '{assembly.GetName().Name}' 中未找到标记为 [Plugin] 的入口类。" +
                "每个插件项目必须有且仅有一个类标记 [Plugin] 特性。");
        }
        return plugin;
    }

    /// <summary>
    /// 尝试扫描程序集，如果未找到插件则返回 null（不抛异常）
    /// </summary>
    public static PluginMetadata? TryScan(Assembly assembly)
    {
        try
        {
            var pluginTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<PluginAttribute>() != null)
                .ToArray();

            // 严格验证：有且仅有一个 [Plugin] 标记类
            if (pluginTypes.Length == 0)
                return null;

            if (pluginTypes.Length > 1)
            {
                var typeNames = string.Join(", ", pluginTypes.Select(t => t.Name));
                throw new InvalidOperationException(
                    $"程序集 '{assembly.GetName().Name}' 中发现 {pluginTypes.Length} 个 [Plugin] 标记类: [{typeNames}]。" +
                    "每个插件项目必须有且仅有一个类标记 [Plugin] 特性。");
            }

            var pluginType = pluginTypes[0];
            var attr = pluginType.GetCustomAttribute<PluginAttribute>()!;

            return new PluginMetadata(
                Id: attr.Id,
                Name: attr.Name,
                Version: attr.Version,
                Description: attr.Description,
                Tags: attr.Tags,
                Instructions: attr.Instructions,
                PluginType: pluginType
            );
        }
        catch (ReflectionTypeLoadException)
        {
            // 程序集类型加载失败，跳过
            return null;
        }
    }
}