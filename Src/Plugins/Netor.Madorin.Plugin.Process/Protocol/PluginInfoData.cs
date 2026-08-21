using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Madorin.Plugin.Process.Protocol;

/// <summary>
/// 插件元数据，<c>get_info</c> 响应中 <c>data</c> 字段的反序列化目标。
/// 由 Generator 从 <see cref="PluginAttribute"/> 和所有 <see cref="ToolAttribute"/> 方法提取。
/// </summary>
public sealed record PluginInfoData
{
    /// <summary>插件唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>插件名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>插件版本。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>插件描述。</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>AI 系统指令片段（可选）。</summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    /// <summary>分类标签。</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>插件能力声明。</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>插件提供给宿主的能力声明；优先于历史兼容字段 capabilities。</summary>
    [JsonPropertyName("providedCapabilities")]
    public IReadOnlyList<string>? ProvidedCapabilities { get; init; }

    /// <summary>插件申请宿主提供的能力列表。</summary>
    [JsonPropertyName("requiredHostCapabilities")]
    public IReadOnlyList<RequiredHostCapabilityData>? RequiredHostCapabilities { get; init; }

    /// <summary>插件配置项声明。</summary>
    [JsonPropertyName("settingsSchema")]
    public IReadOnlyList<PluginSettingData>? SettingsSchema { get; init; }

    /// <summary>工具列表。</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolInfoData> Tools { get; init; } = [];
}

/// <summary>
/// 插件配置项声明。
/// </summary>
public sealed record PluginSettingData
{
    /// <summary>插件内唯一配置键。</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>设置界面显示名称。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>给用户看的配置说明。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>字段类型。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    /// <summary>默认值。</summary>
    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    /// <summary>是否必填。</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>是否敏感。</summary>
    [JsonPropertyName("sensitive")]
    public bool Sensitive { get; init; }

    /// <summary>支持的配置作用域。</summary>
    [JsonPropertyName("scope")]
    public IReadOnlyList<string> Scope { get; init; } = [];

    /// <summary>枚举选项。</summary>
    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>验证规则。</summary>
    [JsonPropertyName("validation")]
    public JsonElement? Validation { get; init; }

    /// <summary>修改后是否要求下次启动才生效。</summary>
    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; init; }
}

/// <summary>
/// 插件申请宿主能力的元数据。
/// </summary>
public sealed record RequiredHostCapabilityData
{
    /// <summary>宿主能力 ID，例如 host.llm.invoke.v1。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>用途 token，例如 memory.extract。</summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    /// <summary>是否为插件核心功能必需能力。</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>展示给用户看的申请原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// 单个工具的元数据。
/// </summary>
public sealed record ToolInfoData
{
    /// <summary>工具名（snake_case）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>插件内短工具名，不包含插件 ID 前缀。</summary>
    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    /// <summary>工具描述，告诉 AI 这个工具做什么。</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>参数列表。</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyList<ParameterInfoData>? Parameters { get; init; }
}

/// <summary>
/// 单个工具参数的元数据。
/// </summary>
public sealed record ParameterInfoData
{
    /// <summary>参数名（snake_case）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>参数类型（<c>string</c> / <c>integer</c> / <c>number</c> / <c>boolean</c>）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    /// <summary>参数描述。</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>是否必填。</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }
}
