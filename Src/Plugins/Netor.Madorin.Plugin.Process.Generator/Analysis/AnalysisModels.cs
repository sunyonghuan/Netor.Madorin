using System.Collections.Generic;

using Microsoft.CodeAnalysis;

namespace Netor.Madorin.Plugin.Process.Generator.Analysis;

internal enum ConfigureMethodKind
{
    None,
    LegacyServicesOnly,
    ServicesAndSettings
}

/// <summary>插件入口类的分析结果。</summary>
internal sealed class PluginClassInfo
{
    public INamedTypeSymbol ClassSymbol { get; }
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }

    /// <summary>插件分类。未声明时为 null。</summary>
    public string? Category { get; }

    /// <summary>插件默认风险级别名称。未声明时为 null。</summary>
    public string? RiskLevel { get; }

    /// <summary>插件默认是否幂等。未声明时为 null。</summary>
    public bool? Idempotent { get; }

    public string[] Tags { get; }

    /// <summary>搜索关键词。</summary>
    public string[] SearchHints { get; }

    public string[] Capabilities { get; }
    public RequiredHostCapabilityInfo[] RequiredHostCapabilities { get; }
    public PluginSettingInfo[] SettingsSchema { get; }

    /// <summary>插件声明的 publishedOps（PluginBus 1.4.0）。</summary>
    public EventOpDeclarationInfo[] PublishedOps { get; }

    /// <summary>插件声明的 subscribedOps（PluginBus 1.4.0）。</summary>
    public EventOpDeclarationInfo[] SubscribedOps { get; }

    public string? Instructions { get; }
    public string? Namespace { get; }
    public string ClassName { get; }

    /// <summary>Configure 方法类型（可选：用户可以不提供）。</summary>
    public ConfigureMethodKind ConfigureMethodKind { get; }

    public PluginClassInfo(
        INamedTypeSymbol classSymbol,
        string id,
        string name,
        string version,
        string description,
        string? category,
        string? riskLevel,
        bool? idempotent,
        string[] tags,
        string[] searchHints,
        string[] capabilities,
        RequiredHostCapabilityInfo[] requiredHostCapabilities,
        PluginSettingInfo[] settingsSchema,
        EventOpDeclarationInfo[] publishedOps,
        EventOpDeclarationInfo[] subscribedOps,
        string? instructions,
        ConfigureMethodKind configureMethodKind)
    {
        ClassSymbol = classSymbol;
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        Category = category;
        RiskLevel = riskLevel;
        Idempotent = idempotent;
        Tags = tags;
        SearchHints = searchHints;
        Capabilities = capabilities;
        RequiredHostCapabilities = requiredHostCapabilities;
        SettingsSchema = settingsSchema;
        PublishedOps = publishedOps;
        SubscribedOps = subscribedOps;
        Instructions = instructions;
        ConfigureMethodKind = configureMethodKind;
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
    public string Op { get; }
    public string? Description { get; }

    public EventOpDeclarationInfo(string op, string? description)
    {
        Op = op;
        Description = description;
    }
}

/// <summary>插件配置项声明的分析结果。</summary>
internal sealed class PluginSettingInfo
{
    public string Key { get; }
    public string? Label { get; }
    public string? Description { get; }
    public string Type { get; }
    public string? DefaultValue { get; }
    public bool Required { get; }
    public bool Sensitive { get; }
    public string[] Scope { get; }
    public string[] Options { get; }
    public double? ValidationMin { get; }
    public double? ValidationMax { get; }
    public int? ValidationMinLength { get; }
    public int? ValidationMaxLength { get; }
    public string? ValidationPattern { get; }
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

/// <summary>插件申请宿主能力的分析结果。</summary>
internal sealed class RequiredHostCapabilityInfo
{
    public string Id { get; }
    public string? Purpose { get; }
    public bool Required { get; }
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

/// <summary>工具类的分析结果。</summary>
internal sealed class ToolClassInfo
{
    public INamedTypeSymbol ClassSymbol { get; }
    public string ClassName { get; }
    public List<ToolMethodInfo> Methods { get; }

    public ToolClassInfo(INamedTypeSymbol classSymbol, List<ToolMethodInfo> methods)
    {
        ClassSymbol = classSymbol;
        ClassName = classSymbol.Name;
        Methods = methods;
    }
}

/// <summary>工具方法的分析结果。</summary>
internal sealed class ToolMethodInfo
{
    public string ClassName { get; }
    public string MethodName { get; }
    public string FullToolName { get; }
    public string MethodSnakeName { get; }
    public string Description { get; }

    /// <summary>覆盖插件分类。null 表示继承插件默认值。</summary>
    public string? Category { get; }

    /// <summary>覆盖插件风险级别。null 表示继承插件默认值。</summary>
    public string? RiskLevel { get; }

    /// <summary>覆盖插件幂等声明。null 表示继承插件默认值。</summary>
    public bool? Idempotent { get; }

    /// <summary>覆盖插件标签。null 表示继承插件默认值。</summary>
    public string[]? Tags { get; }

    /// <summary>覆盖插件搜索关键词。null 表示继承插件默认值。</summary>
    public string[]? SearchHints { get; }

    public List<ToolParamInfo> Parameters { get; }
    public ITypeSymbol ReturnType { get; }
    public bool IsAsync { get; }
    public ITypeSymbol? AsyncInnerType { get; }
    public bool IsValueTask { get; }
    public IMethodSymbol MethodSymbol { get; }

    public ToolMethodInfo(
        string className,
        string methodName,
        string fullToolName,
        string methodSnakeName,
        string description,
        string? category,
        string? riskLevel,
        bool? idempotent,
        string[]? tags,
        string[]? searchHints,
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
        Category = category;
        RiskLevel = riskLevel;
        Idempotent = idempotent;
        Tags = tags;
        SearchHints = searchHints;
        Parameters = parameters;
        ReturnType = returnType;
        IsAsync = isAsync;
        AsyncInnerType = asyncInnerType;
        IsValueTask = isValueTask;
        MethodSymbol = methodSymbol;
    }
}

/// <summary>工具方法参数的分析结果。</summary>
internal sealed class ToolParamInfo
{
    public string ParamName { get; }
    public string JsonName { get; }
    public string Description { get; }
    public bool Required { get; }
    public bool HasDefaultValue { get; }
    public string? DefaultValueLiteral { get; }
    public string JsonType { get; }
    public ITypeSymbol TypeSymbol { get; }
    public string CodeParamName { get; }

    public ToolParamInfo(
        string paramName,
        string jsonName,
        string description,
        bool required,
        bool hasDefaultValue,
        string? defaultValueLiteral,
        string jsonType,
        ITypeSymbol typeSymbol,
        string codeParamName)
    {
        ParamName = paramName;
        JsonName = jsonName;
        Description = description;
        Required = required;
        HasDefaultValue = hasDefaultValue;
        DefaultValueLiteral = defaultValueLiteral;
        JsonType = jsonType;
        TypeSymbol = typeSymbol;
        CodeParamName = codeParamName;
    }
}
