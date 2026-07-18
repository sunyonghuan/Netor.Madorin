using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 持有最近一次 turn 的编排诊断快照，并支持按会话查询，供开发态 UI 只读查询。
/// </summary>
public sealed class ChatOrchestrationDiagnosticsService
{
    private const int MaxSnapshotsPerSession = 6;

    private readonly Lock _gate = new();
    private ChatOrchestrationDiagnosticsSnapshot? _latestSnapshot;
    private readonly Dictionary<string, ChatOrchestrationDiagnosticsSnapshot> _snapshotsBySessionId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatOrchestrationDiagnosticsSnapshot>> _historyBySessionId = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取最近一次 turn 的编排诊断快照。
    /// </summary>
    public ChatOrchestrationDiagnosticsSnapshot? GetLatestSnapshot()
    {
        lock (_gate)
        {
            return _latestSnapshot;
        }
    }

    /// <summary>
    /// 获取指定会话最近一次 turn 的编排诊断快照。
    /// </summary>
    public ChatOrchestrationDiagnosticsSnapshot? GetSnapshot(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_gate)
        {
            return _snapshotsBySessionId.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// 获取指定会话最近几轮 turn 的编排诊断快照，按时间倒序返回。
    /// </summary>
    public IReadOnlyList<ChatOrchestrationDiagnosticsSnapshot> GetRecentSnapshots(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        lock (_gate)
        {
            return _historyBySessionId.TryGetValue(sessionId, out var history)
                ? [.. history]
                : [];
        }
    }

    /// <summary>
    /// 记录 turn 开始时的编排快照。
    /// </summary>
    public void RecordTurnStarted(ConversationEventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        lock (_gate)
        {
            var snapshot = new ChatOrchestrationDiagnosticsSnapshot(
                metadata.SessionId,
                metadata.TurnId,
                metadata.TraceId,
                ConversationTurnStatus.Succeeded,
                metadata.OrchestrationMode,
                metadata.OrchestrationAgentIds is { Count: > 0 } agentIds ? [.. agentIds] : [],
                metadata.OrchestrationWarnings is { Count: > 0 } warnings ? [.. warnings] : [],
                DateTimeOffset.UtcNow,
                false);
            StoreSnapshot(snapshot);
        }
    }

    /// <summary>
    /// 记录 turn 完成时的编排快照。
    /// </summary>
    public void RecordTurnCompleted(
        ConversationEventMetadata metadata,
        ConversationTurnStatus status)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        lock (_gate)
        {
            var snapshot = new ChatOrchestrationDiagnosticsSnapshot(
                metadata.SessionId,
                metadata.TurnId,
                metadata.TraceId,
                status,
                metadata.OrchestrationMode,
                metadata.OrchestrationAgentIds is { Count: > 0 } agentIds ? [.. agentIds] : [],
                metadata.OrchestrationWarnings is { Count: > 0 } warnings ? [.. warnings] : [],
                DateTimeOffset.UtcNow,
                true);
            StoreSnapshot(snapshot);
        }
    }

    private void StoreSnapshot(ChatOrchestrationDiagnosticsSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _snapshotsBySessionId[snapshot.SessionId] = snapshot;

        if (!_historyBySessionId.TryGetValue(snapshot.SessionId, out var history))
        {
            history = [];
            _historyBySessionId[snapshot.SessionId] = history;
        }

        history.RemoveAll(item => string.Equals(item.TurnId, snapshot.TurnId, StringComparison.Ordinal));
        history.Insert(0, snapshot);
        if (history.Count > MaxSnapshotsPerSession)
        {
            history.RemoveRange(MaxSnapshotsPerSession, history.Count - MaxSnapshotsPerSession);
        }
    }
}
