using System.Collections.Concurrent;

using Cortana.Plugins.WsBridge.Core;
using Cortana.Plugins.WsBridge.Models;

namespace Cortana.Plugins.WsBridge.Services;

/// <summary>
/// 管理所有活跃的中转会话，提供增删查接口。
/// </summary>
public sealed class BridgeSessionManager
{
    private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new();

    public BridgeSession? Get(string sessionId) => _sessions.GetValueOrDefault(sessionId);

    public bool TryAdd(BridgeSession session) => _sessions.TryAdd(session.SessionId, session);

    public async Task<bool> RemoveAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        await session.DisposeAsync();
        return true;
    }

    public IReadOnlyList<BridgeSessionInfo> GetAllInfo()
    {
        return _sessions.Values.Select(s => s.ToInfo()).ToList();
    }

    public async Task DisposeAllAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}
