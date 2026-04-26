using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆数据库底层执行抽象，统一管理数据库路径、连接创建、普通命令执行和事务边界。
/// </summary>
public interface IMemoryDatabase
{
    /// <summary>
    /// 获取当前记忆数据库文件的完整路径。
    /// </summary>
    string DatabasePath { get; }

    /// <summary>
    /// 创建并打开一个新的 SQLite 连接，调用方负责释放该连接。
    /// </summary>
    SqliteConnection OpenConnection();

    /// <summary>
    /// 执行一条不返回结果集的数据库命令。
    /// </summary>
    /// <param name="commandText">需要执行的 SQL 命令文本。</param>
    /// <param name="bind">可选的参数绑定委托，用于向命令添加 SQLite 参数。</param>
    void Execute(string commandText, Action<SqliteCommand>? bind = null);

    /// <summary>
    /// 在同一个 SQLite 事务中执行一组数据库操作。
    /// </summary>
    /// <param name="action">接收已打开连接和已创建事务的数据库操作委托。</param>
    void ExecuteInTransaction(Action<SqliteConnection, SqliteTransaction> action);
}
