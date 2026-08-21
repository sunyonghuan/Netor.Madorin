using Microsoft.CodeAnalysis;

namespace Netor.Madorin.Plugin.Native.Generator.Diagnostics;

/// <summary>
/// Native 插件框架的编译时诊断定义。
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "Netor.Cortana.Plugin.Native";

    /// <summary>CNPG003: 工具方法参数使用了不支持的复杂类型。</summary>
    public static readonly DiagnosticDescriptor UnsupportedParameterType = new(
        id: "CNPG003",
        title: "不支持的参数类型",
        messageFormat: "工具方法 '{0}' 的参数 '{1}' 类型为 '{2}'，Native 插件不支持复杂参数类型。仅支持：string, int, long, double, float, decimal, bool",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG004: 不同工具类中存在同名工具（生成的完整工具名冲突）。</summary>
    public static readonly DiagnosticDescriptor DuplicateToolName = new(
        id: "CNPG004",
        title: "工具名称冲突",
        messageFormat: "工具名称 '{0}' 冲突，分别在 {1} 和 {2} 中定义",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG005: [Plugin] 类或 [Tool] 类不是 public 的。</summary>
    public static readonly DiagnosticDescriptor NotPublicClass = new(
        id: "CNPG005",
        title: "类不是 public",
        messageFormat: "类 '{0}' 必须是 public 的",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG005: [Tool] 方法标记在方法上，但所属类未标记 [Tool]。</summary>
    public static readonly DiagnosticDescriptor ToolMethodWithoutToolClass = new(
        id: "CNPG005",
        title: "工具类缺少 [Tool] 标记",
        messageFormat: "类 '{0}' 含有 [Tool] 标记的方法，但类上未标记 [Tool]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG006: 工具方法不是 public 的。</summary>
    public static readonly DiagnosticDescriptor NotPublicMethod = new(
        id: "CNPG006",
        title: "方法不是 public",
        messageFormat: "工具方法 '{0}.{1}' 必须是 public 的",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG007: 工具方法名已经是 snake_case。</summary>
    public static readonly DiagnosticDescriptor AlreadySnakeCase = new(
        id: "CNPG007",
        title: "方法名已是 snake_case",
        messageFormat: "工具方法 '{0}' 的方法名已经是 snake_case 格式，建议使用 PascalCase",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>CNPG008: 方法名转换后的工具名为空或包含非法字符。</summary>
    public static readonly DiagnosticDescriptor InvalidToolName = new(
        id: "CNPG008",
        title: "无效的工具名称",
        messageFormat: "方法 '{0}' 转换后的工具名 '{1}' 无效（空或包含非法字符）",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG009: [Plugin] 缺少必填属性 Id 或 Name。</summary>
    public static readonly DiagnosticDescriptor MissingRequiredAttribute = new(
        id: "CNPG009",
        title: "缺少必填属性",
        messageFormat: "[Plugin] 缺少必填属性 '{0}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG010: 项目中发现多个 Startup 类。</summary>
    public static readonly DiagnosticDescriptor MultiplePluginClasses = new(
        id: "CNPG010",
        title: "多个插件入口类",
        messageFormat: "项目中发现多个 [Plugin] 标记的 Startup 类：{0}。一个项目只允许一个插件入口类",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG011: Startup 类必须是 static partial class。</summary>
    public static readonly DiagnosticDescriptor NotStaticPartialClass = new(
        id: "CNPG011",
        title: "不是 static partial class",
        messageFormat: "Startup 类 '{0}' 必须是 static partial class",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG012: Startup 类缺少 Configure 方法。</summary>
    public static readonly DiagnosticDescriptor MissingConfigureMethod = new(
        id: "CNPG012",
        title: "缺少 Configure 方法",
        messageFormat: "Startup 类 '{0}' 缺少 public static void Configure(IPluginBuilder builder) 方法",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG019: [Plugin] 的 Id 包含非法字符（仅允许小写字母、数字和下划线）。</summary>
    public static readonly DiagnosticDescriptor InvalidPluginId = new(
        id: "CNPG019",
        title: "无效的 Plugin Id",
        messageFormat: "[Plugin] 的 Id '{0}' 包含非法字符，仅允许小写字母、数字和下划线",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG020: 工具方法返回了自定义类型但项目中缺少 PluginJsonContext。</summary>
    public static readonly DiagnosticDescriptor MissingPluginJsonContext = new(
        id: "CNPG020",
        title: "缺少 PluginJsonContext",
        messageFormat: "工具方法 '{0}' 返回自定义类型 '{1}'，需要在项目中手写 PluginJsonContext 并注册 [JsonSerializable(typeof({1}))]。详见：https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>CNPG021: json 类型插件配置默认值不是合法 JSON。</summary>
    public static readonly DiagnosticDescriptor InvalidPluginSettingJsonDefaultValue = new(
        id: "CNPG021",
        title: "无效的插件配置 JSON 默认值",
        messageFormat: "插件配置项 '{0}' 的 DefaultValue 必须是合法 JSON 值",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
