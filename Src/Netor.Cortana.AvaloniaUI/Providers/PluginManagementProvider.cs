using Netor.Cortana.Plugin;

using System.Text;

namespace Netor.Cortana.AvaloniaUI.Providers;

/// <summary>
/// 插件管理工具提供者，向 AI 提供已加载插件的查询、卸载与重载能力，用于插件热更新。
/// </summary>
internal sealed class PluginManagementProvider(
    ILogger<PluginManagementProvider> logger,
    PluginLoader pluginLoader) : AIContextProvider
{
    private readonly List<AITool> _tools = [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
            RegisterTools();

        return ValueTask.FromResult(new AIContext
        {
            Instructions = BuildInstructions(),
            Tools = _tools
        });
    }

    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_loaded_plugins",
            description: "Lists all currently loaded plugins (including native plugins and MCP services), returning a numbered list. Includes plugin directory names (for unloading/reloading).",
            method: ListLoadedPlugins));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_unload_plugin",
            description: "Unloads a specified plugin, releasing its file locks. Parameter: dirName (plugin directory name, obtained via sys_list_loaded_plugins). Allows replacing plugin files before reloading.",
            method: (string dirName) => UnloadPlugin(dirName)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_reload_plugin",
            description: "Reloads a specified plugin (unloads then loads). Parameter: dirName (plugin directory name, obtained via sys_list_loaded_plugins). Used to reload after replacing plugin files.",
            method: (string dirName) => ReloadPluginAsync(dirName)));
    }

    private static string BuildInstructions() =>
        """
        You can manage loaded plugins: view list, unload, and reload.
        Plugin update workflow: Call sys_unload_plugin to unload the target plugin → Replace plugin files → Call sys_reload_plugin to reload.
        Unloading a plugin terminates its child processes and releases file locks, allowing file replacement afterwards.
        """;

    private string ListLoadedPlugins()
    {
        var sb = new StringBuilder();

        // 原生插件（Native）
        var nativePlugins = pluginLoader.GetActivePlugins();
        if (nativePlugins.Count > 0)
        {
            sb.AppendLine("原生插件：");
            for (int i = 0; i < nativePlugins.Count; i++)
            {
                var p = nativePlugins[i];
                sb.AppendLine($"  {i + 1}. {p.Name} (v{p.Version}) [id={p.Id}]");
            }
        }

        // MCP 服务
        var mcpServers = pluginLoader.GetActiveMcpServers();
        if (mcpServers.Count > 0)
        {
            sb.AppendLine("MCP 服务：");
            for (int i = 0; i < mcpServers.Count; i++)
            {
                var m = mcpServers[i];
                sb.AppendLine($"  {i + 1}. {m.Name} [id={m.Id}]");
            }
        }

        // 插件目录
        var dirNames = pluginLoader.GetLoadedPluginDirNames();
        if (dirNames.Count > 0)
        {
            sb.AppendLine("插件目录（用于卸载/重载）：");
            foreach (var dir in dirNames)
                sb.AppendLine($"  - {dir}");
        }

        if (sb.Length == 0)
            return "当前没有已加载的插件。";

        return sb.ToString();
    }

    private string UnloadPlugin(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName))
            return "✗ 插件目录名不能为空。";

        try
        {
            pluginLoader.UnloadPlugin(dirName);
            logger.LogInformation("已卸载插件：{DirName}", dirName);
            return $"✓ 已卸载插件「{dirName}」，文件占用已释放，现在可以替换文件。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "卸载插件失败：{DirName}", dirName);
            return $"✗ 卸载插件失败：{ex.Message}";
        }
    }

    private async Task<string> ReloadPluginAsync(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName))
            return "✗ 插件目录名不能为空。";

        try
        {
            await pluginLoader.ReloadPluginAsync(dirName);
            logger.LogInformation("已重载插件：{DirName}", dirName);
            return $"✓ 已重载插件「{dirName}」。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "重载插件失败：{DirName}", dirName);
            return $"✗ 重载插件失败：{ex.Message}";
        }
    }
}