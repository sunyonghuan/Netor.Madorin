using Microsoft.Data.Sqlite;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆数据库 SQLite 访问辅助方法集合。
/// </summary>
/// <remarks>
/// 这些扩展方法集中处理参数绑定和空值读取，避免各个表服务重复编写 DBNull 判断逻辑。
/// </remarks>
internal static class MemoryDb
{
    /// <summary>
    /// 向 SQLite 命令添加参数，并自动把 null 转换为 <see cref="DBNull.Value" />。
    /// </summary>
    /// <param name="command">要添加参数的 SQLite 命令。</param>
    /// <param name="name">参数名称。</param>
    /// <param name="value">参数值；为 null 时写入数据库空值。</param>
    public static void AddParameter(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    /// <summary>
    /// 从当前读取行中获取可空字符串。
    /// </summary>
    /// <param name="reader">SQLite 数据读取器。</param>
    /// <param name="ordinal">字段序号。</param>
    /// <returns>字段值；数据库值为 NULL 时返回 null。</returns>
    public static string? GetNullableString(this SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// 从当前读取行中获取整数，数据库值为 NULL 时返回默认值。
    /// </summary>
    /// <param name="reader">SQLite 数据读取器。</param>
    /// <param name="ordinal">字段序号。</param>
    /// <param name="defaultValue">数据库值为 NULL 时使用的默认值。</param>
    /// <returns>读取到的整数或默认值。</returns>
    public static int GetInt32OrDefault(this SqliteDataReader reader, int ordinal, int defaultValue = 0)
    {
        return reader.IsDBNull(ordinal) ? defaultValue : reader.GetInt32(ordinal);
    }

    /// <summary>
    /// 从当前读取行中获取长整数，数据库值为 NULL 时返回默认值。
    /// </summary>
    /// <param name="reader">SQLite 数据读取器。</param>
    /// <param name="ordinal">字段序号。</param>
    /// <param name="defaultValue">数据库值为 NULL 时使用的默认值。</param>
    /// <returns>读取到的长整数或默认值。</returns>
    public static long GetInt64OrDefault(this SqliteDataReader reader, int ordinal, long defaultValue = 0)
    {
        return reader.IsDBNull(ordinal) ? defaultValue : reader.GetInt64(ordinal);
    }

    /// <summary>
    /// 从当前读取行中获取双精度浮点数，数据库值为 NULL 时返回默认值。
    /// </summary>
    /// <param name="reader">SQLite 数据读取器。</param>
    /// <param name="ordinal">字段序号。</param>
    /// <param name="defaultValue">数据库值为 NULL 时使用的默认值。</param>
    /// <returns>读取到的双精度浮点数或默认值。</returns>
    public static double GetDoubleOrDefault(this SqliteDataReader reader, int ordinal, double defaultValue = 0)
    {
        return reader.IsDBNull(ordinal) ? defaultValue : reader.GetDouble(ordinal);
    }

    /// <summary>
    /// 从当前读取行中获取布尔值，数据库值为 NULL 时返回默认值。
    /// </summary>
    /// <param name="reader">SQLite 数据读取器。</param>
    /// <param name="ordinal">字段序号。</param>
    /// <param name="defaultValue">数据库值为 NULL 时使用的默认值。</param>
    /// <returns>读取到的布尔值或默认值；SQLite 中非零整数视为 true。</returns>
    public static bool GetBooleanOrDefault(this SqliteDataReader reader, int ordinal, bool defaultValue = false)
    {
        return reader.IsDBNull(ordinal) ? defaultValue : reader.GetInt32(ordinal) != 0;
    }
}
