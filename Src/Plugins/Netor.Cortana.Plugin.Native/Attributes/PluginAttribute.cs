namespace Netor.Cortana.Plugin;

/// <summary>
/// 标记原生插件入口类。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>插件唯一标识。</summary>
    public required string Id { get; init; }

    /// <summary>插件名称。</summary>
    public required string Name { get; init; }

    /// <summary>插件版本。</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>插件描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>分类标签。</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>AI 系统指令片段。</summary>
    public string? Instructions { get; init; }
}