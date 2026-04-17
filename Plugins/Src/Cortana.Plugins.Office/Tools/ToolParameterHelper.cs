using System.Text.Json;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// 工具层参数转换辅助方法。
/// 插件框架仅支持 string 和 int 参数，此类负责将框架类型转换为业务类型。
/// </summary>
internal static class ToolParameterHelper
{
    /// <summary>将 int 参数转换为布尔值，非 0 为 true。</summary>
    public static bool IsTrue(int value) => value != 0;

    /// <summary>将字符串参数解析为字符串数组，支持 JSON 数组和逗号分隔格式。</summary>
    public static string[]? ParseArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // 优先尝试 JSON 数组格式（AOT 安全的源码生成反序列化）
        if (value.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize(value, PluginJsonContext.Default.StringArray);
            }
            catch
            {
                // JSON 解析失败时回退到逗号分隔
            }
        }

        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>判断字符串参数是否为空（框架要求所有参数必填，空字符串表示未提供）。</summary>
    public static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value);
}
