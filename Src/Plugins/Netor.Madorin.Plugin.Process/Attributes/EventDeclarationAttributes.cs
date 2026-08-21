namespace Netor.Madorin.Plugin;

/// <summary>
/// 声明插件会发布的事件 op（PluginBus 1.4.0）。详见 docs/已完成功能规划/插件事件广播/README.md §2.3。
/// 多次叠加，每个 op 一条；op 首段须等于其归属 topic（OQ3 OQ4）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PublishesEventAttribute : Attribute
{
	/// <summary>构造一条事件发布声明。</summary>
	/// <param name="op">事件 op，例如 voice.kws.detected.v1。</param>
	public PublishesEventAttribute(string op)
	{
		Op = op;
	}

	/// <summary>事件 op，例如 voice.kws.detected.v1。</summary>
	public string Op { get; }

	/// <summary>事件用途说明（运维审计用，非校验依据）。</summary>
	public string? Description { get; init; }
}

/// <summary>
/// 声明插件会订阅的事件 op（PluginBus 1.4.0）。详见 docs/已完成功能规划/插件事件广播/README.md §2.3。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class SubscribesEventAttribute : Attribute
{
	/// <summary>构造一条事件订阅声明。</summary>
	/// <param name="op">事件 op，例如 voice.stt.partial.v1。</param>
	public SubscribesEventAttribute(string op)
	{
		Op = op;
	}

	/// <summary>事件 op，例如 voice.stt.partial.v1。</summary>
	public string Op { get; }

	/// <summary>订阅理由（运维审计用，非校验依据）。</summary>
	public string? Reason { get; init; }
}
