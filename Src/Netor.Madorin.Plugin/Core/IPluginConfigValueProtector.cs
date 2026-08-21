namespace Netor.Madorin.Plugin;

/// <summary>
/// 保护插件配置中的敏感值。
/// </summary>
public interface IPluginConfigValueProtector
{
    /// <summary>
    /// 判断存储值是否为已保护格式。
    /// </summary>
    bool IsProtectedValue(string? value);

    /// <summary>
    /// 将明文配置值转换为可存储的受保护格式。
    /// </summary>
    string Protect(string value);

    /// <summary>
    /// 读取配置的有效明文值；非受保护格式会原样返回以兼容历史数据。
    /// </summary>
    string GetEffectiveValue(string storedValue);
}
