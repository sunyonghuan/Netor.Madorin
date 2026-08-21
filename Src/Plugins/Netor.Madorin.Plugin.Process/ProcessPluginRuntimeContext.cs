namespace Netor.Madorin.Plugin;

/// <summary>
/// 进程插件宿主提供的轻量运行时上下文。
/// </summary>
public sealed class ProcessPluginRuntimeContext
{
    /// <summary>
    /// 当前插件运行实例的唯一标识。
    /// </summary>
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 宿主在 init 阶段下发的运行时配置。
    /// </summary>
    public PluginSettings Settings { get; init; } = default!;
}
