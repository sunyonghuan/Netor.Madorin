namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆数据库路径配置。
/// </summary>
public interface IMemoryDatabaseOptions
{
    /// <summary>
    /// 存储提供程序。
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// 数据库存放目录。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// 数据库文件名。
    /// </summary>
    string DatabaseFileName { get; }

    /// <summary>
    /// 数据库连接字符串。
    /// </summary>
    string? ConnectionString { get; }
}
