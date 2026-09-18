using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Madorin.Plugin;

/// <summary>
/// 插件运行时模式。
/// </summary>
public enum PluginRuntime
{
    /// <summary>
    /// .NET 托管插件（AssemblyLoadContext 加载）。
    /// </summary>
    [Display(Name = "dotnet")]
    Dotnet,

    /// <summary>
    /// 原生 DLL 插件（NativeLibrary 加载）。
    /// </summary>
    [Display(Name = "native")]
    Native,

    /// <summary>
    /// 子进程插件（stdin/stdout JSON-RPC）。
    /// </summary>
    [Display(Name = "process")]
    Process
}

/// <summary>
/// plugin.json 清单文件的反序列化模型。
/// 包含通用字段和三种运行时模式的扩展字段。
/// </summary>
public sealed record PluginManifest
{
    // ──────── 通用字段 ────────

    /// <summary>插件唯一标识（建议 reverse-domain 格式）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>插件显示名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>语义版本号。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>插件描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>插件分类，例如 file-system / database / development。工具未覆盖时继承此值。</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>插件默认风险级别。工具未覆盖时继承此值。</summary>
    [JsonPropertyName("riskLevel")]
    public ToolRiskLevel? RiskLevel { get; init; }

    /// <summary>插件默认是否幂等。工具未覆盖时继承此值。</summary>
    [JsonPropertyName("idempotent")]
    public bool? Idempotent { get; init; }

    /// <summary>运行时模式：dotnet / native / process。</summary>
    [JsonPropertyName("runtime")]
    public PluginRuntime Runtime { get; init; }

    /// <summary>要求的最低宿主版本。</summary>
    [JsonPropertyName("minHostVersion")]
    public string? MinHostVersion { get; init; }

    /// <summary>插件引用的 Abstractions 版本。</summary>
    [JsonPropertyName("abstractionsVersion")]
    public string? AbstractionsVersion { get; init; }

    /// <summary>分类标签，用于向旧版插件兼容地声明能力。工具未覆盖时继承此值。</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>搜索关键词。按插件功能和用途填写，工具未覆盖时继承此值。</summary>
    [JsonPropertyName("searchHints")]
    public IReadOnlyList<string> SearchHints { get; init; } = [];

    /// <summary>插件导出的工具清单。未写分类字段的工具继承插件级默认值。</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<PluginToolDescriptor> Tools { get; init; } = [];

    /// <summary>插件能力 ID 列表，例如 voice.kws / voice.stt / voice.tts。</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>插件提供给宿主的能力 ID 列表；优先于历史兼容字段 capabilities。</summary>
    [JsonPropertyName("providedCapabilities")]
    public IReadOnlyList<string> ProvidedCapabilities { get; init; } = [];

    /// <summary>插件申请宿主提供的能力列表。</summary>
    [JsonPropertyName("requiredHostCapabilities")]
    public IReadOnlyList<RequiredHostCapability> RequiredHostCapabilities { get; init; } = [];

    /// <summary>插件声明的配置项 Schema。只描述字段，不包含真实配置值。</summary>
    [JsonPropertyName("settingsSchema")]
    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; init; } = [];

    /// <summary>插件声明的事件发布契约（PluginBus 1.4.0）。op 首段须等于其归属 topic。</summary>
    [JsonPropertyName("publishedOps")]
    public IReadOnlyList<PublishedOpDeclaration> PublishedOps { get; init; } = [];

    /// <summary>插件声明的事件订阅契约（PluginBus 1.4.0）。运行帧 subscribedOps 缺省时按 topic 全收。</summary>
    [JsonPropertyName("subscribedOps")]
    public IReadOnlyList<SubscribedOpDeclaration> SubscribedOps { get; init; } = [];

    /// <summary>
    /// 规范化可选集合字段，兼容旧版插件清单缺省字段或显式 null 的情况。
    /// </summary>
    public PluginManifest Normalize() => this with
    {
        Tags = Tags ?? [],
        SearchHints = SearchHints ?? [],
        Tools = NormalizeTools(Tools),
        Capabilities = Capabilities ?? [],
        ProvidedCapabilities = ProvidedCapabilities ?? [],
        RequiredHostCapabilities = RequiredHostCapabilities ?? [],
        SettingsSchema = NormalizeSettingsSchema(SettingsSchema),
        PublishedOps = PublishedOps ?? [],
        SubscribedOps = SubscribedOps ?? []
    };

    // ──────── dotnet 模式扩展字段 ────────

    /// <summary>入口程序集文件名（如 WeatherPlugin.dll）。</summary>
    [JsonPropertyName("assemblyName")]
    public string? AssemblyName { get; init; }

    /// <summary>插件目标框架（如 net10.0）。</summary>
    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; init; }

    // ──────── native 模式扩展字段 ────────

    /// <summary>原生 DLL 文件名。</summary>
    [JsonPropertyName("libraryName")]
    public string? LibraryName { get; init; }

    // ──────── process 模式扩展字段 ────────

    /// <summary>启动命令（相对路径基于插件目录）。</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    // ──────── 校验 ────────

    /// <summary>
    /// 校验清单文件的必填字段是否完整。
    /// </summary>
    public bool Validate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = "缺少必填字段 'id'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "缺少必填字段 'name'";
            return false;
        }

        switch (Runtime)
        {
            case PluginRuntime.Dotnet when string.IsNullOrWhiteSpace(AssemblyName):
                error = "dotnet 模式缺少 'assemblyName' 字段";
                return false;

            case PluginRuntime.Native when string.IsNullOrWhiteSpace(LibraryName):
                error = "native 模式缺少 'libraryName' 字段";
                return false;

            case PluginRuntime.Process when string.IsNullOrWhiteSpace(Command):
                error = "process 模式缺少 'command' 字段";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<PluginToolDescriptor> NormalizeTools(
        IReadOnlyList<PluginToolDescriptor>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        var normalized = new List<PluginToolDescriptor>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                continue;
            }

            normalized.Add(tool.Normalize());
        }

        return normalized;
    }

    private static IReadOnlyList<PluginSettingDescriptor> NormalizeSettingsSchema(
        IReadOnlyList<PluginSettingDescriptor>? settingsSchema)
    {
        if (settingsSchema is null || settingsSchema.Count == 0)
        {
            return [];
        }

        var normalized = new List<PluginSettingDescriptor>(settingsSchema.Count);
        foreach (var descriptor in settingsSchema)
        {
            if (descriptor is null)
            {
                continue;
            }

            normalized.Add(descriptor.Normalize());
        }

        return normalized;
    }
}

/// <summary>
/// 插件 publishedOps 单条声明（PluginBus 1.4.0）。op 首段须等于归属 topic。
/// 详见 docs/已完成功能规划/插件事件广播/README.md §2.3。
/// </summary>
public sealed record PublishedOpDeclaration
{
    /// <summary>事件 op，例如 voice.kws.detected.v1。</summary>
    [JsonPropertyName("op")]
    public string Op { get; init; } = string.Empty;

    /// <summary>事件用途说明（运维审计用，非校验依据）。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// 插件 subscribedOps 单条声明（PluginBus 1.4.0）。
/// </summary>
public sealed record SubscribedOpDeclaration
{
    /// <summary>事件 op，例如 voice.stt.partial.v1。</summary>
    [JsonPropertyName("op")]
    public string Op { get; init; } = string.Empty;

    /// <summary>订阅理由（运维审计用，非校验依据）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// 插件向宿主申请使用的能力声明。
/// </summary>
public sealed record RequiredHostCapability
{
    /// <summary>宿主能力 ID，例如 host.llm.invoke.v1。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>用途 token，例如 memory.extract。</summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    /// <summary>是否为插件启动或核心功能必需能力。</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>展示给用户看的申请原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// 插件配置项声明。该模型用于主程序生成设置界面，不承载用户真实配置值。
/// </summary>
public sealed record PluginSettingDescriptor
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

    /// <summary>字段类型，例如 string / secret / number / boolean / enum / path / json。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    /// <summary>默认值。使用 JsonElement 保留原始 JSON 类型。</summary>
    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    /// <summary>是否必填。</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>是否敏感，敏感值不得回显或进入日志。</summary>
    [JsonPropertyName("sensitive")]
    public bool Sensitive { get; init; }

    /// <summary>支持的配置作用域。</summary>
    [JsonPropertyName("scope")]
    public IReadOnlyList<string> Scope { get; init; } = [];

    /// <summary>枚举选项。</summary>
    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>验证规则。使用 JsonElement 保留后续扩展空间。</summary>
    [JsonPropertyName("validation")]
    public JsonElement? Validation { get; init; }

    /// <summary>修改后是否要求下次启动才生效。</summary>
    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; init; }

    /// <summary>
    /// 规范化可选集合字段，兼容插件清单缺省字段或显式 null 的情况。
    /// </summary>
    public PluginSettingDescriptor Normalize() => this with
    {
        Type = string.IsNullOrWhiteSpace(Type) ? "string" : Type,
        Scope = Scope ?? [],
        Options = Options ?? []
    };
}

/// <summary>
/// plugin.json 中的单个工具声明。未写出的分类字段表示继承插件级默认值。
/// </summary>
public sealed record PluginToolDescriptor
{
    /// <summary>工具名（snake_case 短名）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>工具描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>一层 JSON Schema，描述工具入参。</summary>
    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; init; }

    /// <summary>覆盖插件级分类。缺省表示继承。</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>覆盖插件级风险级别。缺省表示继承。</summary>
    [JsonPropertyName("riskLevel")]
    public ToolRiskLevel? RiskLevel { get; init; }

    /// <summary>覆盖插件级幂等声明。缺省表示继承。</summary>
    [JsonPropertyName("idempotent")]
    public bool? Idempotent { get; init; }

    /// <summary>覆盖插件级标签。缺省表示继承。</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>覆盖插件级搜索关键词。缺省表示继承。</summary>
    [JsonPropertyName("searchHints")]
    public IReadOnlyList<string>? SearchHints { get; init; }

    /// <summary>
    /// 规范化可选集合字段。null 仍表示继承，不改写成空数组。
    /// </summary>
    public PluginToolDescriptor Normalize() => this with
    {
        Name = Name ?? string.Empty
    };
}
