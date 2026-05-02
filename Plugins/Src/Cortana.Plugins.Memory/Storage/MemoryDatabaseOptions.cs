namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 默认记忆数据库路径配置实现。
/// </summary>
public sealed class MemoryDatabaseOptions(string dataDirectory, string databaseFileName = "memory.db") : IMemoryDatabaseOptions
{
    /// <inheritdoc />
    public string DataDirectory { get; } = string.IsNullOrWhiteSpace(dataDirectory)
        ? AppContext.BaseDirectory
        : dataDirectory;

    /// <inheritdoc />
    public string DatabaseFileName { get; } = string.IsNullOrWhiteSpace(databaseFileName)
        ? "memory.db"
        : databaseFileName;
}
