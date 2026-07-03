namespace Netor.Cortana.Plugin.Native;

/// <summary>
/// 插件配置项类型。
/// </summary>
public enum PluginSettingType
{
    /// <summary>普通文本。</summary>
    String,

    /// <summary>敏感文本，不应回显。</summary>
    Secret,

    /// <summary>数字。</summary>
    Number,

    /// <summary>布尔值。</summary>
    Boolean,

    /// <summary>枚举选项。</summary>
    Enum,

    /// <summary>文件或目录路径。</summary>
    Path,

    /// <summary>JSON 文本。</summary>
    Json
}
