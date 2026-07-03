using Netor.Cortana.AI.Hitl;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.MeetingMode.Hitl;

/// <summary>
/// 将会议会话数据服务适配为公共 HITL 上下文。
/// </summary>
public sealed class MeetingHitlContext : IHitlContext
{
    private readonly MeetingSessionService _sessions;

    public MeetingHitlContext(MeetingSessionService sessions, string meetingId)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        ContextId = meetingId ?? throw new ArgumentNullException(nameof(meetingId));
    }

    public string ContextId { get; }

    public void SetPending(string requestId, string kind, string snapshotJson)
    {
        _sessions.SetPending(ContextId, requestId, kind, snapshotJson);
    }

    public void ClearPending()
    {
        _sessions.ClearPending(ContextId);
    }
}
