using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// PowerShell 会话管理器 - 管理所有活跃的交互式会话
/// </summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly ILogger<SessionRegistry> _logger;
    private readonly Dictionary<string, ExecutionSession> _sessions = new();
    private readonly Lock _lock = new();
    private readonly System.Threading.Timer _cleanupTimer;

    public SessionRegistry(ILogger<SessionRegistry> logger)
    {
        _logger = logger;
        // 每 30 秒清理一次空闲超时的会话
        _cleanupTimer = new System.Threading.Timer(CleanupIdleSessions, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 创建并注册一个新会话
    /// </summary>
    public ExecutionSession CreateSession(string sessionType, string? host = null, string? username = null, string? password = null, string? privateKeyPath = null)
    {
        var session = new ExecutionSession(sessionType, host, username, password, privateKeyPath, _logger);

        lock (_lock)
        {
            _sessions[session.Id] = session;
        }

        _logger.LogInformation("会话已创建: {SessionId} ({Type})", session.Id, sessionType);
        return session;
    }

    /// <summary>
    /// 获取会话
    /// </summary>
    public ExecutionSession? GetSession(string sessionId)
    {
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return session;
        }
    }

    /// <summary>
    /// 移除并关闭会话
    /// </summary>
    public async Task RemoveSessionAsync(string sessionId)
    {
        ExecutionSession? session;
        lock (_lock)
        {
            _sessions.Remove(sessionId, out session);
        }

        if (session != null)
        {
            _logger.LogInformation("会话已移除: {SessionId}", sessionId);
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// 列出所有活跃会话
    /// </summary>
    public List<(string Id, string Type, string AuthType, string? Host, DateTime CreatedAt, ExecutionSessionState State, string StatusMessage)> GetActiveSessions()
    {
        lock (_lock)
        {
            return _sessions
                .Where(x => x.Value.IsActive)
                .Select(x => (x.Key, x.Value.Type, x.Value.AuthenticationMode, x.Value.Host, x.Value.CreatedAt, x.Value.State, x.Value.StatusMessage))
                .ToList();
        }
    }

    /// <summary>
    /// 清理空闲超时的会话
    /// </summary>
    private void CleanupIdleSessions(object? state)
    {
        List<ExecutionSession> toDispose;

        lock (_lock)
        {
            const int idleTimeoutMs = 300_000; // 5 分钟
            var now = DateTime.Now;
            var expiredKeys = _sessions
                .Where(x => !x.Value.IsActive || (now - x.Value.LastActivityAt).TotalMilliseconds > idleTimeoutMs)
                .Select(x => x.Key)
                .ToList();

            toDispose = new List<ExecutionSession>(expiredKeys.Count);
            foreach (var key in expiredKeys)
            {
                if (_sessions.Remove(key, out var session))
                {
                    toDispose.Add(session);
                    _logger.LogInformation("空闲会话已清理: {SessionId}", key);
                }
            }
        }

        // 在锁外异步释放资源
        foreach (var session in toDispose)
        {
            _ = DisposeSessionSafeAsync(session);
        }
    }

    private async Task DisposeSessionSafeAsync(ExecutionSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "释放会话资源失败: {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// 关闭所有会话
    /// </summary>
    public async Task CloseAllSessionsAsync()
    {
        List<ExecutionSession> sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        // 在锁外逐个释放
        foreach (var session in sessions)
        {
            await DisposeSessionSafeAsync(session);
        }

        _logger.LogInformation("所有会话已关闭");
    }

    public async ValueTask DisposeAsync()
    {
        await _cleanupTimer.DisposeAsync();
        await CloseAllSessionsAsync();
    }
}
