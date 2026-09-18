namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 统一标记工具类和工具方法。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>工具名称。</summary>
    public string? Name { get; init; }

    /// <summary>工具描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>覆盖插件级 <see cref="PluginAttribute.Category"/>。未写则继承插件默认值。</summary>
    public string? Category { get; init; }

    /// <summary>覆盖插件级 <see cref="PluginAttribute.RiskLevel"/>。未写则继承插件默认值。</summary>
    public ToolRiskLevel? RiskLevel { get; init; }

    /// <summary>覆盖插件级 <see cref="PluginAttribute.Idempotent"/>。未写则继承插件默认值。</summary>
    public bool? Idempotent { get; init; }

    /// <summary>覆盖插件级 <see cref="PluginAttribute.Tags"/>。未写则继承插件默认值。</summary>
    public string[]? Tags { get; init; }

    /// <summary>覆盖插件级 <see cref="PluginAttribute.SearchHints"/>。未写则继承插件默认值。</summary>
    public string[]? SearchHints { get; init; }
}