namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 默认记忆数据库路径配置实现。
/// </summary>
public sealed class MemoryDatabaseOptions(string dataDirectory, string databaseFileName = "memory.db", string? connectionString = null, string provider = "sqlite") : IMemoryDatabaseOptions
{
    /// <inheritdoc />
    public string Provider { get; } = string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider;

    /// <inheritdoc />
    public string DataDirectory { get; } = string.IsNullOrWhiteSpace(dataDirectory)
        ? AppContext.BaseDirectory
        : dataDirectory;

    /// <inheritdoc />
    public string DatabaseFileName { get; } = string.IsNullOrWhiteSpace(databaseFileName)
        ? "memory.db"
        : databaseFileName;

    /// <inheritdoc />
    public string? ConnectionString { get; } = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
}
