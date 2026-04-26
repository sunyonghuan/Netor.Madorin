namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示记忆系统的运行配置或策略配置项。
/// </summary>
public sealed class MemorySetting
{
    /// <summary>
    /// 配置记录唯一标识。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 配置所属智能体标识；为空表示全局默认配置。
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// 配置所属工作区标识；为空表示不限定工作区。
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// 配置键，例如 decay.defaultRate 或 recall.maxWindowCount。
    /// </summary>
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>
    /// 配置值，按字符串保存，读取时根据 ValueType 转换。
    /// </summary>
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>
    /// 配置值类型，例如 string、int、double、bool 或 json。
    /// </summary>
    public string ValueType { get; set; } = "string";

    /// <summary>
    /// 配置分类，例如 decay、recall、supply、abstraction 或 governance。
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 配置项中文说明。
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 是否启用该配置项。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 数据结构版本号。
    /// </summary>
    public int SchemaVersion { get; set; } = 3;

    /// <summary>
    /// 当前记录版本号。
    /// </summary>
    public int RecordVersion { get; set; } = 1;

    /// <summary>
    /// 配置创建时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>
    /// 配置最后更新时间，采用 ISO 8601 UTC 格式。
    /// </summary>
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
