using System.Collections.Concurrent;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// 管理 PluginBus 客户端订阅关系，并提供按 topic 查询目标连接的能力。
/// 阶段 5B Phase 4 起额外维护客户端声明的 capabilities 集合（决策 5B-D 能力声明）。
/// </summary>
internal sealed class PluginBusSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new(StringComparer.Ordinal);

    // 阶段 5B Phase 4：每个客户端的 capability 声明集合（subscribe 帧 capabilities 字段解析后写入）
    // 详见 04-实施阶段.md §5B.4 / Phase 4 实施计划 §5.2。
    private readonly ConcurrentDictionary<string, HashSet<string>> _capabilities = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取当前至少订阅一个 topic 的客户端数量。
    /// </summary>
    public int Count => _subscriptions.Count;

    /// <summary>
    /// 注册或更新客户端订阅的 topic 集合。
    /// </summary>
    /// <param name="clientId">客户端连接标识。</param>
    /// <param name="topics">客户端请求订阅的 topic 集合。</param>
    /// <returns>规范化后的 topic 集合。</returns>
    public string[] Subscribe(string clientId, IEnumerable<string> topics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(topics);

        var normalized = topics
            .Select(static topic => topic.Trim())
            .Where(static topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            normalized = [CortanaWsEndpoints.ConversationTopic];
        }

        _subscriptions[clientId] = new HashSet<string>(normalized, StringComparer.Ordinal);
        return normalized;
    }

    /// <summary>
    /// 移除客户端全部订阅。
    /// </summary>
    /// <param name="clientId">客户端连接标识。</param>
    public void Remove(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        _subscriptions.TryRemove(clientId, out _);
        _capabilities.TryRemove(clientId, out _);
    }

    /// <summary>
    /// 清空所有订阅关系。
    /// </summary>
    public void Clear()
    {
        _subscriptions.Clear();
        _capabilities.Clear();
    }

    /// <summary>
    /// 查询订阅了指定 topic 的客户端标识。
    /// </summary>
    /// <param name="topic">目标 topic。</param>
    /// <returns>已订阅该 topic 的客户端标识序列。</returns>
    public IEnumerable<string> GetSubscribers(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) yield break;

        foreach (var pair in _subscriptions)
        {
            if (pair.Value.Contains(topic))
            {
                yield return pair.Key;
            }
        }
    }

    /// <summary>
    /// 判断客户端是否订阅了指定 topic。
    /// </summary>
    /// <param name="clientId">客户端连接标识。</param>
    /// <param name="topic">目标 topic。</param>
    /// <returns>如果已订阅则为 <see langword="true"/>。</returns>
    public bool IsSubscribed(string clientId, string topic)
    {
        return _subscriptions.TryGetValue(clientId, out var topics) && topics.Contains(topic);
    }

    // ──── 阶段 5B Phase 4：capabilities 能力声明 ────

    /// <summary>
    /// 记录客户端在 subscribe 帧中声明的 capabilities 集合（决策 5B-D：能力声明而非强制版本号锁）。
    /// 重复调用会覆盖旧值；空集合视为客户端未声明任何能力。
    /// </summary>
    /// <param name="clientId">客户端连接标识。</param>
    /// <param name="capabilities">客户端声明的 capability token 列表。</param>
    public void SetCapabilities(string clientId, IEnumerable<string> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(capabilities);

        var normalized = capabilities
            .Select(static cap => cap.Trim())
            .Where(static cap => !string.IsNullOrWhiteSpace(cap))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _capabilities[clientId] = new HashSet<string>(normalized, StringComparer.Ordinal);
    }

    /// <summary>
    /// 获取客户端声明的 capabilities 集合的拷贝；未注册时返回空数组。
    /// </summary>
    public IReadOnlyCollection<string> GetCapabilities(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return Array.Empty<string>();
        return _capabilities.TryGetValue(clientId, out var caps)
            ? caps.ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// 判断客户端是否声明了指定 capability。
    /// </summary>
    public bool HasCapability(string clientId, string capability)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(capability))
            return false;
        return _capabilities.TryGetValue(clientId, out var caps) && caps.Contains(capability);
    }
}
