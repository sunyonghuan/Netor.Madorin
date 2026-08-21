using System.Globalization;
using System.Text;

namespace Netor.Madorin.Plugin.Process.Debugging;

/// <summary>
/// AOT 安全的工具参数构建器。由 Generator 生成的强类型 Debugger 方法使用，
/// 将用户传入的参数拼装成符合工具协议的 JSON 字符串。
/// <para>
/// 不依赖 <see cref="System.Text.Json.JsonSerializer"/> 和反射，
/// 直接字符串拼接，所有转义手动处理。
/// </para>
/// </summary>
public sealed class DebuggerArgs
{
    private readonly List<(string Key, string JsonValue)> _entries = [];

    private DebuggerArgs() { }

    /// <summary>创建一个空参数构建器。</summary>
    public static DebuggerArgs Build() => new();

    /// <summary>添加字符串参数（<c>null</c> 序列化为 JSON null）。</summary>
    public DebuggerArgs Add(string key, string? value)
    {
        _entries.Add((key, value is null ? "null" : EncodeString(value)));
        return this;
    }

    /// <summary>添加整数参数。</summary>
    public DebuggerArgs Add(string key, int value)
    {
        _entries.Add((key, value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>添加长整数参数。</summary>
    public DebuggerArgs Add(string key, long value)
    {
        _entries.Add((key, value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>添加浮点参数。</summary>
    public DebuggerArgs Add(string key, double value)
    {
        _entries.Add((key, value.ToString("R", CultureInfo.InvariantCulture)));
        return this;
    }

    /// <summary>添加布尔参数。</summary>
    public DebuggerArgs Add(string key, bool value)
    {
        _entries.Add((key, value ? "true" : "false"));
        return this;
    }

    /// <summary>添加可空整数参数。</summary>
    public DebuggerArgs Add(string key, int? value)
        => value.HasValue ? Add(key, value.Value) : AddNull(key);

    /// <summary>添加可空长整数参数。</summary>
    public DebuggerArgs Add(string key, long? value)
        => value.HasValue ? Add(key, value.Value) : AddNull(key);

    /// <summary>添加可空浮点参数。</summary>
    public DebuggerArgs Add(string key, double? value)
        => value.HasValue ? Add(key, value.Value) : AddNull(key);

    /// <summary>添加可空布尔参数。</summary>
    public DebuggerArgs Add(string key, bool? value)
        => value.HasValue ? Add(key, value.Value) : AddNull(key);

    /// <summary>仅当可空整数有值时添加参数。</summary>
    public DebuggerArgs AddIfNotNull(string key, int? value)
        => value.HasValue ? Add(key, value.Value) : this;

    /// <summary>仅当可空长整数有值时添加参数。</summary>
    public DebuggerArgs AddIfNotNull(string key, long? value)
        => value.HasValue ? Add(key, value.Value) : this;

    /// <summary>仅当可空浮点数有值时添加参数。</summary>
    public DebuggerArgs AddIfNotNull(string key, double? value)
        => value.HasValue ? Add(key, value.Value) : this;

    /// <summary>仅当可空浮点数有值时添加参数。</summary>
    public DebuggerArgs AddIfNotNull(string key, float? value)
        => value.HasValue ? Add(key, value.Value) : this;

    /// <summary>仅当可空布尔有值时添加参数。</summary>
    public DebuggerArgs AddIfNotNull(string key, bool? value)
        => value.HasValue ? Add(key, value.Value) : this;

    private DebuggerArgs AddNull(string key)
    {
        _entries.Add((key, "null"));
        return this;
    }

    /// <summary>序列化为 JSON 对象字符串。</summary>
    public string ToJson()
    {
        if (_entries.Count == 0)
            return "{}";

        var sb = new StringBuilder(64);
        sb.Append('{');
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var (key, jsonValue) = _entries[i];
            sb.Append(EncodeString(key));
            sb.Append(':');
            sb.Append(jsonValue);
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 将字符串编码为合法的 JSON 字符串字面量（含引号）。
    /// 手动转义 <c>\</c>、<c>"</c>、控制字符。
    /// </summary>
    private static string EncodeString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (ch < 0x20)
                        sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:X4}");
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
