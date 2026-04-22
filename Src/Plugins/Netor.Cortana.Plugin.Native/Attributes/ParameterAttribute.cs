namespace Netor.Cortana.Plugin;

/// <summary>
/// 标记工具方法参数的描述信息。
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class ParameterAttribute : Attribute
{
    /// <summary>参数名称。</summary>
    public string? Name { get; init; }

    /// <summary>参数描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>是否必填。</summary>
    public bool Required { get; init; } = true;
}