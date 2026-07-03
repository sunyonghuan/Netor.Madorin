using System.Reflection;

namespace Netor.Cortana.Plugin.Native.Debugger.Discovery;

/// <summary>
/// 工具扫描器 - 扫描程序集中所有标记 [Tool] 的方法
/// </summary>
public static class ToolScanner
{
    /// <summary>
    /// 扫描程序集中的所有工具
    /// </summary>
    public static ToolRegistry Scan(Assembly assembly)
    {
        var registry = new ToolRegistry();

        foreach (var type in assembly.GetTypes())
        {
            // 跳过非公开类、抽象类、静态类
            if (!type.IsClass || type.IsAbstract || !type.IsPublic)
                continue;

            // 获取所有公开实例方法
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                // 检查方法是否标记了 [Tool]
                var toolAttr = method.GetCustomAttribute<ToolAttribute>();
                if (toolAttr != null)
                {
                    var toolName = toolAttr.Name ?? $"{type.Name}_{method.Name}";
                    registry.Register(new ToolMetadata(
                        ToolName: toolName,
                        DeclaringType: type,
                        MethodInfo: method,
                        Parameters: method.GetParameters()
                    ));
                }
            }
        }

        return registry;
    }
}