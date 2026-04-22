using Netor.Cortana.Plugin.Native.Debugger.Discovery;
using Netor.Cortana.Plugin.Native.Debugger.Hosting;
using Netor.Cortana.Plugin.Native.Debugger.Repl;

using System.Reflection;

namespace Netor.Cortana.Plugin.Native.Debugger;

/// <summary>
/// 插件调试入口
/// </summary>
public static class PluginDebugRunner
{
    /// <summary>
    /// 自动发现插件并运行交互式调试（零配置）
    /// </summary>
    public static async Task RunAsync(Action<DebugOptions>? configure = null)
    {
        var assembly = DiscoverPluginAssembly();
        await RunAsync(assembly, configure);
    }

    /// <summary>
    /// 指定程序集运行交互式调试
    /// </summary>
    public static async Task RunAsync(Assembly pluginAssembly, Action<DebugOptions>? configure = null)
    {
        var options = new DebugOptions();
        configure?.Invoke(options);

        await using var host = new DebugPluginHost(pluginAssembly, options);
        var repl = new ReplLoop(host);
        await repl.RunAsync();
    }

    /// <summary>
    /// 自动发现插件程序集：扫描输出目录中的 DLL，查找标记 [Plugin] 的程序集
    /// </summary>
    private static Assembly DiscoverPluginAssembly()
    {
        var baseDir = AppContext.BaseDirectory;
        var dlls = Directory.GetFiles(baseDir, "*.dll");

        var plugins = new List<(Assembly Assembly, PluginMetadata Meta)>();

        foreach (var dll in dlls)
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(dll);
                var name = assemblyName.Name;

                if (name == null || IsFrameworkAssembly(name))
                    continue;

                var assembly = Assembly.Load(assemblyName);
                var meta = PluginScanner.TryScan(assembly);

                if (meta != null)
                    plugins.Add((assembly, meta));
            }
            catch
            {
                // Skip assemblies that can't be loaded
            }
        }

        return plugins.Count switch
        {
            0 => throw new InvalidOperationException(
                "未找到任何插件。请确保测试项目引用了插件项目。"),
            1 => plugins[0].Assembly,
            _ => throw new InvalidOperationException(
                $"发现 {plugins.Count} 个插件: {string.Join(", ", plugins.Select(p => p.Meta.Name))}。调试时请只引用一个插件项目。")
        };
    }

    private static bool IsFrameworkAssembly(string name)
    {
        return name.StartsWith("System.", StringComparison.Ordinal) ||
               name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
               name.StartsWith("netstandard", StringComparison.Ordinal) ||
               name.StartsWith("mscorlib", StringComparison.Ordinal) ||
               name.StartsWith("Netor.Cortana.Plugin.Native", StringComparison.Ordinal) ||
               name.StartsWith("Netor.Extensions", StringComparison.Ordinal);
    }
}