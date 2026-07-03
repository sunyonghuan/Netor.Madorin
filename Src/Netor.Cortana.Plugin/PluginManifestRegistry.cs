using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 维护已加载插件的事件契约声明（来自 plugin.json publishedOps / subscribedOps）。
/// 由 PluginBusBroadcastDispatcher 在弱声明校验时查询；v1 仅 LogWarning 不强拒。
///
/// DI 单例：在 PluginServiceExtensions.AddCortanaPlugin 注册；由 PluginLoader 在
/// load / reload / unload 时调用 Register / Unregister / Clear。
///
/// PluginBus 1.4.0 OQ1：放在 Netor.Cortana.Plugin 程序集贴近 PluginLoader 写入方；
/// BroadcastDispatcher 跨程序集消费走现有间接路径 Networks → AI → Plugin。
/// 详见 docs/已完成功能规划/插件事件广播/README.md §3.2 与 03-宿主转发器实现.md §四。
/// </summary>
public sealed class PluginManifestRegistry
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _publishedOps =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, HashSet<string>> _subscribedOps =
        new(StringComparer.Ordinal);

    private readonly ILogger<PluginManifestRegistry> _logger;

    public PluginManifestRegistry(ILogger<PluginManifestRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// 由 PluginLoader 在每次插件 load / reload 后调用。重复调用覆盖旧记录。
    /// </summary>
    public void Register(string pluginId, IEnumerable<string> publishedOps, IEnumerable<string> subscribedOps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        _publishedOps[pluginId] = new HashSet<string>(
            publishedOps ?? Array.Empty<string>(), StringComparer.Ordinal);
        _subscribedOps[pluginId] = new HashSet<string>(
            subscribedOps ?? Array.Empty<string>(), StringComparer.Ordinal);

        _logger.LogDebug(
            "PluginManifestRegistry registered. PluginId={PluginId} PublishedCount={PubCount} SubscribedCount={SubCount}",
            pluginId, _publishedOps[pluginId].Count, _subscribedOps[pluginId].Count);
    }

    /// <summary>
    /// 由 PluginLoader 在卸载插件后调用。
    /// </summary>
    public void Unregister(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;
        _publishedOps.TryRemove(pluginId, out _);
        _subscribedOps.TryRemove(pluginId, out _);
    }

    /// <summary>
    /// 配合 UnloadAllPlugins 整体清空。
    /// </summary>
    public void Clear()
    {
        _publishedOps.Clear();
        _subscribedOps.Clear();
    }

    /// <summary>
    /// 弱声明校验：发布方 plugin.json 是否声明了该 op。
    /// </summary>
    public bool HasPublishedOp(string pluginId, string op)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(op))
            return false;
        return _publishedOps.TryGetValue(pluginId, out var ops) && ops.Contains(op);
    }

    /// <summary>
    /// 弱声明校验：订阅方 plugin.json 是否声明了该 op。
    /// </summary>
    public bool HasSubscribedOp(string pluginId, string op)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(op))
            return false;
        return _subscribedOps.TryGetValue(pluginId, out var ops) && ops.Contains(op);
    }
}
