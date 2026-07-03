namespace Netor.Cortana.Entitys;

/// <summary>
/// 系统设置键值对实体，以键名作为主键（Id）存储所有可配置的系统参数。
/// Id 格式采用"分组.配置项"点分格式，如 "SherpaOnnx.KeywordsThreshold"。
/// </summary>
public class SystemSettingsEntity : BaseEntity
{
    // Id 继承自 BaseEntity，作为设置的唯一键名
    // 例如: "SherpaOnnx.KeywordsThreshold", "Tts.Speed"

    /// <summary>
    /// 设置分组名称，用于界面分类展示。
    /// 例如: "语音唤醒", "语音识别", "语音合成", "对话历史", "系统"
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// 设置项的中文显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 设置项描述/说明。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 设置值（JSON 字符串存储，支持 string/int/float/bool）。
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 默认值，用于重置操作。
    /// </summary>
    public string DefaultValue { get; set; } = string.Empty;

    /// <summary>
    /// 值类型标识：string / int / float / bool
    /// </summary>
    public string ValueType { get; set; } = "string";

    /// <summary>
    /// 排序权重，界面展示用，数值越小越靠前。
    /// </summary>
    public int SortOrder { get; set; }
}
