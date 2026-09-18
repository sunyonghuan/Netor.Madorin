namespace Netor.Madorin.Plugin;

/// <summary>
/// 标记插件入口类。一个项目有且只有一个。
/// <para>
/// Generator 会从此 Attribute 提取元数据，生成 Process 通道所需入口代码和 plugin.json。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginAttribute : Attribute
{
	/// <summary>
	/// 插件唯一标识。
	/// <para>仅允许小写字母、数字和下划线。</para>
	/// </summary>
	public required string Id { get; init; }

	/// <summary>
	/// 插件名称。
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 插件版本。
	/// </summary>
	public string Version { get; init; } = "1.0.0";

	/// <summary>
	/// 插件描述。
	/// </summary>
	public string Description { get; init; } = "";

	/// <summary>
	/// 插件分类，例如 file-system / database / development。所有工具默认继承。
	/// </summary>
	public string? Category { get; init; }

	/// <summary>
	/// 插件默认风险级别。未写则不出现在清单中；工具可通过 <see cref="ToolAttribute.RiskLevel"/> 覆盖。
	/// </summary>
	public ToolRiskLevel? RiskLevel { get; init; }

	/// <summary>
	/// 插件默认是否幂等。未写则不出现在清单中；工具可通过 <see cref="ToolAttribute.Idempotent"/> 覆盖。
	/// </summary>
	public bool? Idempotent { get; init; }

	/// <summary>
	/// 分类标签。所有工具默认继承，个别工具可覆盖。
	/// </summary>
	public string[] Tags { get; init; } = [];

	/// <summary>
	/// 搜索关键词。按插件功能和用途填写，所有工具默认继承，个别工具可覆盖。
	/// </summary>
	public string[] SearchHints { get; init; } = [];

	/// <summary>
	/// 插件能力声明，例如 <c>voice.tts</c>。
	/// </summary>
	public string[] Capabilities { get; init; } = [];

	/// <summary>
	/// AI 系统指令片段。
	/// </summary>
	public string? Instructions { get; init; }
}
