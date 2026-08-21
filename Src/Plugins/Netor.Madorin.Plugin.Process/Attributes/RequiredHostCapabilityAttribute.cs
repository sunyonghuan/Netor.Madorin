namespace Netor.Madorin.Plugin;

/// <summary>
/// 声明插件希望宿主提供的运行时能力。声明仅用于设置 UI 与授权摘要生成，不等同于已授权。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class RequiredHostCapabilityAttribute : Attribute
{
	/// <summary>宿主能力 ID，例如 <c>host.llm.invoke.v1</c>。</summary>
	public required string Id { get; init; }

	/// <summary>用途 token，例如 <c>memory.extract</c>。</summary>
	public string? Purpose { get; init; }

	/// <summary>是否为插件核心功能必需能力。</summary>
	public bool Required { get; init; }

	/// <summary>展示给用户看的申请原因。</summary>
	public string? Reason { get; init; }
}
