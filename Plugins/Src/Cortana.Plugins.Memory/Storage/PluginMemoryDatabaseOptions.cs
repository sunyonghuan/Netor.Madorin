using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 将宿主插件配置适配为记忆数据库路径配置。
/// </summary>
public sealed class PluginMemoryDatabaseOptions(PluginSettings settings) : IMemoryDatabaseOptions
{
    private readonly MemoryPluginSettings _pluginSettings = MemoryPluginSettingsLoader.Load(settings.DataDirectory);

    /// <inheritdoc />
    public string Provider => string.IsNullOrWhiteSpace(_pluginSettings.Storage.Provider)
        ? "sqlite"
        : _pluginSettings.Storage.Provider;

    /// <inheritdoc />
    public string DataDirectory => string.IsNullOrWhiteSpace(settings.DataDirectory)
        ? ResolveDataDirectory()
        : settings.DataDirectory;

    /// <inheritdoc />
    public string DatabaseFileName => string.IsNullOrWhiteSpace(_pluginSettings.Storage.Sqlite.DatabaseFileName)
        ? "memory.db"
        : _pluginSettings.Storage.Sqlite.DatabaseFileName;

    /// <inheritdoc />
    public string? ConnectionString => string.IsNullOrWhiteSpace(_pluginSettings.Storage.Sqlite.ConnectionString)
        ? null
        : _pluginSettings.Storage.Sqlite.ConnectionString;

    private string ResolveDataDirectory()
    {
        return string.IsNullOrWhiteSpace(_pluginSettings.Storage.Sqlite.DataDirectory)
            ? AppContext.BaseDirectory
            : _pluginSettings.Storage.Sqlite.DataDirectory;
    }
}
