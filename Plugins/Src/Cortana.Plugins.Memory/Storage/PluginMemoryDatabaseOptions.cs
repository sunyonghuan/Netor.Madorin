using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 将宿主插件配置适配为记忆数据库路径配置。
/// </summary>
public sealed class PluginMemoryDatabaseOptions(PluginSettings settings) : IMemoryDatabaseOptions
{
    /// <inheritdoc />
    public string DataDirectory => string.IsNullOrWhiteSpace(settings.DataDirectory)
        ? AppContext.BaseDirectory
        : settings.DataDirectory;

    /// <inheritdoc />
    public string DatabaseFileName => "memory.db";
}
