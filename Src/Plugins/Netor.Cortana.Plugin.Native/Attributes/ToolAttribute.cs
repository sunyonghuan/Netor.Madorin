namespace Netor.Cortana.Plugin;

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
}