namespace Netor.Cortana.Plugin;

/// <summary>
/// 统一标记工具类和工具方法。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
	/// <summary>
	/// 标记在方法上时：工具名称。不填则从方法名自动转换 PascalCase 到 snake_case。
	/// 标记在类上时忽略。
	/// </summary>
	public string? Name { get; init; }

	/// <summary>
	/// 工具描述。
	/// </summary>
	public string Description { get; init; } = "";
}