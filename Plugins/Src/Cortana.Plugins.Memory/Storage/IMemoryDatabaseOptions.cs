namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆数据库路径配置。
/// </summary>
public interface IMemoryDatabaseOptions
{
    /// <summary>
    /// 数据库存放目录。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// 数据库文件名。
    /// </summary>
    string DatabaseFileName { get; }
}
