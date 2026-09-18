namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 标记原生插件入口类。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>插件唯一标识。</summary>
    public string Id { get; init; }

    /// <summary>插件名称。</summary>
    public string Name { get; init; }

    /// <summary>插件版本。</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>插件描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>插件分类，例如 file-system / database / development。所有工具默认继承。</summary>
    public string? Category { get; init; }

    /// <summary>插件默认风险级别。未写则不出现在清单中；工具可通过 <see cref="ToolAttribute.RiskLevel"/> 覆盖。</summary>
    public ToolRiskLevel RiskLevel { get; init; }

    /// <summary>插件默认是否幂等。未写则不出现在清单中；工具可通过 <see cref="ToolAttribute.Idempotent"/> 覆盖。</summary>
    public bool Idempotent { get; init; }

    /// <summary>分类标签。所有工具默认继承，个别工具可覆盖。</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>搜索关键词。按插件功能和用途填写，所有工具默认继承，个别工具可覆盖。</summary>
    public string[] SearchHints { get; init; } = [];

    /// <summary>插件提供给宿主的能力声明，例如 voice.tts。</summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>AI 系统指令片段。</summary>
    public string? Instructions { get; init; }
}
