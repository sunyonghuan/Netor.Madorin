using System.Text.Json.Serialization;
using System.Text.Json;

namespace Netor.Cortana.Plugin.Native;
/// 描述原生插件的元数据和工具列表。
/// </summary>
public sealed record NativePluginInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; init; }

    [JsonPropertyName("providedCapabilities")]
    public List<string>? ProvidedCapabilities { get; init; }

    [JsonPropertyName("requiredHostCapabilities")]
    public List<NativeRequiredHostCapability>? RequiredHostCapabilities { get; init; }

    [JsonPropertyName("settingsSchema")]
    public List<NativePluginSetting>? SettingsSchema { get; init; }

    [JsonPropertyName("tools")]
    public List<NativeToolInfo>? Tools { get; init; }
}

/// <summary>
/// 原生插件配置项声明。
/// </summary>
public sealed record NativePluginSetting
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("sensitive")]
    public bool Sensitive { get; init; }

    [JsonPropertyName("scope")]
    public IReadOnlyList<string> Scope { get; init; } = [];

    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options { get; init; } = [];

    [JsonPropertyName("validation")]
    public JsonElement? Validation { get; init; }

    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; init; }
}

/// <summary>
/// 原生插件申请宿主能力的声明。
/// </summary>
public sealed record NativeRequiredHostCapability
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// 原生插件导出的单个工具描述。
/// </summary>
public sealed record NativeToolInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public List<NativeToolParameter>? Parameters { get; init; }
}

/// <summary>
/// 原生工具的参数描述。
/// </summary>
public sealed record NativeToolParameter
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 参数类型：string / number / boolean / integer / array / object。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }
}
