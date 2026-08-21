using System.Collections.Generic;

using Microsoft.CodeAnalysis;

namespace Netor.Madorin.Plugin.Native.Generator.Analysis;

/// <summary>
/// 插件入口类的分析结果。
/// </summary>
internal sealed class PluginClassInfo
{
    /// <summary>插件入口类的符号。</summary>
    public INamedTypeSymbol ClassSymbol { get; }

    /// <summary>插件 Id。</summary>
    public string Id { get; }

    /// <summary>插件名称。</summary>
    public string Name { get; }

    /// <summary>插件版本。</summary>
    public string Version { get; }

    /// <summary>插件描述。</summary>
    public string Description { get; }

    /// <summary>分类标签。</summary>
    public string[] Tags { get; }

    /// <summary>插件提供给宿主的能力声明。</summary>
    public string[] Capabilities { get; }

    /// <summary>插件申请宿主提供的能力声明。</summary>
    public RequiredHostCapabilityInfo[] RequiredHostCapabilities { get; }

    /// <summary>插件配置项声明。</summary>
    public PluginSettingInfo[] SettingsSchema { get; }

    /// <summary>插件声明的 publishedOps（PluginBus 1.4.0）。</summary>
    public EventOpDeclarationInfo[] PublishedOps { get; }

    /// <summary>插件声明的 subscribedOps（PluginBus 1.4.0）。</summary>
    public EventOpDeclarationInfo[] SubscribedOps { get; }

    /// <summary>AI 指令。</summary>
    public string? Instructions { get; }

    /// <summary>插件入口类所在的命名空间。</summary>
    public string? Namespace { get; }

    /// <summary>插件入口类名。</summary>
    public string ClassName { get; }

    public PluginClassInfo(
        INamedTypeSymbol classSymbol,
        string id,
        string name,
        string version,
        string description,
        string[] tags,
        string[] capabilities,
        RequiredHostCapabilityInfo[] requiredHostCapabilities,
        PluginSettingInfo[] settingsSchema,
        EventOpDeclarationInfo[] publishedOps,
        EventOpDeclarationInfo[] subscribedOps,
        string? instructions)
    {
        ClassSymbol = classSymbol;
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        Tags = tags;
        Capabilities = capabilities;
        RequiredHostCapabilities = requiredHostCapabilities;
        SettingsSchema = settingsSchema;
        PublishedOps = publishedOps;
        SubscribedOps = subscribedOps;
        Instructions = instructions;
        Namespace = classSymbol.ContainingNamespace?.IsGlobalNamespace == true
            ? null
            : classSymbol.ContainingNamespace?.ToDisplayString();
        ClassName = classSymbol.Name;
    }
}

/// <summary>
/// PluginBus 1.4.0 事件 op 声明（[PublishesEvent] / [SubscribesEvent]）。
/// </summary>
internal sealed class EventOpDeclarationInfo
{
    /// <summary>事件 op，例如 voice.kws.detected.v1。</summary>
    public string Op { get; }

    /// <summary>事件用途说明（运维审计用，非校验依据）。</summary>
    public string? Description { get; }

    public EventOpDeclarationInfo(string op, string? description)
    {
        Op = op;
        Description = description;
    }
}

/// <summary>
/// 插件配置项声明的分析结果。
/// </summary>
internal sealed class PluginSettingInfo
{
    /// <summary>配置键。</summary>
    public string Key { get; }

    /// <summary>显示名称。</summary>
    public string? Label { get; }

    /// <summary>说明。</summary>
    public string? Description { get; }

    /// <summary>配置类型。</summary>
    public string Type { get; }

    /// <summary>默认值字符串。</summary>
    public string? DefaultValue { get; }

    /// <summary>是否必填。</summary>
    public bool Required { get; }

    /// <summary>是否敏感。</summary>
    public bool Sensitive { get; }

    /// <summary>配置作用域。</summary>
    public string[] Scope { get; }

    /// <summary>枚举选项。</summary>
    public string[] Options { get; }

    /// <summary>最小值。</summary>
    public double? ValidationMin { get; }

    /// <summary>最大值。</summary>
    public double? ValidationMax { get; }

    /// <summary>最小长度。</summary>
    public int? ValidationMinLength { get; }

    /// <summary>最大长度。</summary>
    public int? ValidationMaxLength { get; }

    /// <summary>正则校验。</summary>
    public string? ValidationPattern { get; }

    /// <summary>修改后是否要求重启。</summary>
    public bool RestartRequired { get; }

    public PluginSettingInfo(
        string key,
        string? label,
        string? description,
        string type,
        string? defaultValue,
        bool required,
        bool sensitive,
        string[] scope,
        string[] options,
        double? validationMin,
        double? validationMax,
        int? validationMinLength,
        int? validationMaxLength,
        string? validationPattern,
        bool restartRequired)
    {
        Key = key;
        Label = label;
        Description = description;
        Type = type;
        DefaultValue = defaultValue;
        Required = required;
        Sensitive = sensitive;
        Scope = scope;
        Options = options;
        ValidationMin = validationMin;
        ValidationMax = validationMax;
        ValidationMinLength = validationMinLength;
        ValidationMaxLength = validationMaxLength;
        ValidationPattern = validationPattern;
        RestartRequired = restartRequired;
    }
}

/// <summary>
/// 插件申请宿主能力的分析结果。
/// </summary>
internal sealed class RequiredHostCapabilityInfo
{
    /// <summary>宿主能力 ID。</summary>
    public string Id { get; }

    /// <summary>用途 token。</summary>
    public string? Purpose { get; }

    /// <summary>是否为核心功能必需能力。</summary>
    public bool Required { get; }

    /// <summary>展示给用户看的申请原因。</summary>
    public string? Reason { get; }

    public RequiredHostCapabilityInfo(
        string id,
        string? purpose,
        bool required,
        string? reason)
    {
        Id = id;
        Purpose = purpose;
        Required = required;
        Reason = reason;
    }
}

/// <summary>
/// 工具类的分析结果。
/// </summary>
internal sealed class ToolClassInfo
{
    /// <summary>工具类的符号。</summary>
    public INamedTypeSymbol ClassSymbol { get; }

    /// <summary>工具类名。</summary>
    public string ClassName { get; }

    /// <summary>工具类中的工具方法列表。</summary>
    public List<ToolMethodInfo> Methods { get; }

    public ToolClassInfo(
        INamedTypeSymbol classSymbol,
        List<ToolMethodInfo> methods)
    {
        ClassSymbol = classSymbol;
        ClassName = classSymbol.Name;
        Methods = methods;
    }
}

/// <summary>
/// 工具方法的分析结果。
/// </summary>
internal sealed class ToolMethodInfo
{
    /// <summary>所属工具类名。</summary>
    public string ClassName { get; }

    /// <summary>方法名。</summary>
    public string MethodName { get; }

    /// <summary>生成的完整工具名（含 plugin_id 前缀）。</summary>
    public string FullToolName { get; }

    /// <summary>方法级 snake_case 名称（不含前缀）。</summary>
    public string MethodSnakeName { get; }

    /// <summary>工具描述。</summary>
    public string Description { get; }

    /// <summary>参数列表。</summary>
    public List<ToolParamInfo> Parameters { get; }

    /// <summary>返回类型符号。</summary>
    public ITypeSymbol ReturnType { get; }

    /// <summary>是否异步方法。</summary>
    public bool IsAsync { get; }

    /// <summary>异步方法的内部返回类型（Task&lt;T&gt; 的 T）。</summary>
    public ITypeSymbol? AsyncInnerType { get; }

    /// <summary>是否为 ValueTask。</summary>
    public bool IsValueTask { get; }

    /// <summary>方法符号。</summary>
    public IMethodSymbol MethodSymbol { get; }

    public ToolMethodInfo(
        string className,
        string methodName,
        string fullToolName,
        string methodSnakeName,
        string description,
        List<ToolParamInfo> parameters,
        ITypeSymbol returnType,
        bool isAsync,
        ITypeSymbol? asyncInnerType,
        bool isValueTask,
        IMethodSymbol methodSymbol)
    {
        ClassName = className;
        MethodName = methodName;
        FullToolName = fullToolName;
        MethodSnakeName = methodSnakeName;
        Description = description;
        Parameters = parameters;
        ReturnType = returnType;
        IsAsync = isAsync;
        AsyncInnerType = asyncInnerType;
        IsValueTask = isValueTask;
        MethodSymbol = methodSymbol;
    }
}

/// <summary>
/// 工具方法参数的分析结果。
/// </summary>
internal sealed class ToolParamInfo
{
    /// <summary>参数名（方法参数名或 [ParameterAttribute] 指定）。</summary>
    public string ParamName { get; }

    /// <summary>生成的参数名（snake_case）。</summary>
    public string JsonName { get; }

    /// <summary>参数描述。</summary>
    public string Description { get; }

    /// <summary>是否必填。</summary>
    public bool Required { get; }

    /// <summary>JSON Schema 类型。</summary>
    public string JsonType { get; }

    /// <summary>参数类型符号。</summary>
    public ITypeSymbol TypeSymbol { get; }

    /// <summary>原始方法参数名（代码中使用）。</summary>
    public string CodeParamName { get; }

    /// <summary>参数缺失时传给方法的 C# 默认值表达式。</summary>
    public string DefaultValueExpression { get; }

    public ToolParamInfo(
        string paramName,
        string jsonName,
        string description,
        bool required,
        string jsonType,
        ITypeSymbol typeSymbol,
        string codeParamName,
        string defaultValueExpression)
    {
        ParamName = paramName;
        JsonName = jsonName;
        Description = description;
        Required = required;
        JsonType = jsonType;
        TypeSymbol = typeSymbol;
        CodeParamName = codeParamName;
        DefaultValueExpression = defaultValueExpression;
    }
}
