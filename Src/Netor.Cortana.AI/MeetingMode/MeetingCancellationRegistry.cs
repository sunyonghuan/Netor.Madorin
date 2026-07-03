using System.Collections.Concurrent;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议运行时取消令牌注册表。
/// </summary>
public sealed class MeetingCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningMeetings = new();

    /// <summary>注册正在执行的会议，返回 false 表示同一会议已有执行轮次。</summary>
    public bool TryRegister(string meetingId, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(cts);

        return _runningMeetings.TryAdd(meetingId, cts);
    }

    /// <summary>移除会议运行令牌。</summary>
    public bool TryUnregister(string meetingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        return _runningMeetings.TryRemove(meetingId, out _);
    }

    /// <summary>取消会议当前执行轮次。</summary>
    public Task<bool> CancelAsync(string meetingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        return _runningMeetings.TryGetValue(meetingId, out var cts)
            ? CancelRegisteredAsync(cts)
            : Task.FromResult(false);
    }

    private static async Task<bool> CancelRegisteredAsync(CancellationTokenSource cts)
    {
        await cts.CancelAsync().ConfigureAwait(false);
        return true;
    }
}
