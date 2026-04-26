using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 基于 SQLite 的记忆数据库底层执行服务。
/// </summary>
/// <remarks>
/// 该类型只负责数据库路径解析、连接创建、命令执行和事务提交/回滚，
/// 不承载任何具体表结构或业务查询语义。
/// </remarks>
public sealed class SqliteMemoryDatabase(PluginSettings settings, ILogger<SqliteMemoryDatabase> logger) : IMemoryDatabase
{
    private string? _databasePath;

    /// <summary>
    /// 获取记忆数据库文件路径，并在首次访问时确保数据目录存在。
    /// </summary>
    public string DatabasePath
    {
        get
        {
            if (_databasePath is not null) return _databasePath;

            var directory = settings.DataDirectory;
            if (string.IsNullOrWhiteSpace(directory)) directory = AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            _databasePath = Path.Combine(directory, "memory.db");
            return _databasePath;
        }
    }

    /// <summary>
    /// 创建并打开一个指向当前记忆数据库文件的 SQLite 连接。
    /// </summary>
    /// <returns>已经打开的 SQLite 连接。</returns>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// 执行一条不返回结果集的 SQLite 命令。
    /// </summary>
    /// <param name="commandText">需要执行的 SQL 命令文本。</param>
    /// <param name="bind">可选的参数绑定委托，用于避免字符串拼接 SQL。</param>
    public void Execute(string commandText, Action<SqliteCommand>? bind = null)
    {
        if (string.IsNullOrWhiteSpace(commandText)) throw new ArgumentException("数据库命令不能为空。", nameof(commandText));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 在单个事务中执行一组 SQLite 操作，成功时提交，失败时回滚并记录错误。
    /// </summary>
    /// <param name="action">需要在事务中执行的数据库操作。</param>
    public void ExecuteInTransaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            action(connection, transaction);
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "执行记忆数据库事务失败：{Path}", DatabasePath);
            transaction.Rollback();
            throw;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "执行记忆数据库事务失败：{Path}", DatabasePath);
            transaction.Rollback();
            throw;
        }
    }
}
