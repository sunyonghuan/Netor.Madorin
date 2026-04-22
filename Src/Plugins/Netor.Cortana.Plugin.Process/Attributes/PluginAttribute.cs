namespace Netor.Cortana.Plugin;

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
	/// 分类标签。
	/// </summary>
	public string[] Tags { get; init; } = [];

	/// <summary>
	/// AI 系统指令片段。
	/// </summary>
	public string? Instructions { get; init; }
}