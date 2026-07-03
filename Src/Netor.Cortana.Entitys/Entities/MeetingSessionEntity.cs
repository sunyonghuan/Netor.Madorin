namespace Netor.Cortana.Entitys;

/// <summary>
/// 会议模式会话实体（MeetingSessions 表）。
/// </summary>
public sealed class MeetingSessionEntity
{
    /// <summary>会议 ID（GUID(N)）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的 ChatSessions.Id。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>工作区 ID。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>会议主题。</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>主持人 Agent ID。</summary>
    public string HostAgentId { get; set; } = "system-meeting-host";

    /// <summary>参会者 JSON 快照。</summary>
    public string ParticipantsJson { get; set; } = "[]";

    /// <summary>0=进行中，1=等待用户回复，2=已结束，3=已取消/散会。</summary>
    public int Status { get; set; }

    /// <summary>AF StreamingRun.RunId。</summary>
    public string? RunId { get; set; }

    /// <summary>HITL 挂起请求 ID。</summary>
    public string? PendingRequestId { get; set; }

    /// <summary>HITL 挂起请求类型。</summary>
    public string? PendingRequestKind { get; set; }

    /// <summary>HITL 挂起请求 JSON 快照。</summary>
    public string? PendingRequestData { get; set; }

    /// <summary>最终会议总结 Markdown。</summary>
    public string? FinalSummaryMd { get; set; }

    /// <summary>会议建立时锁定的主持人 Provider。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>会议建立时锁定的主持人 Model。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>更新时间（Unix 毫秒）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>结束/散会时间（Unix 毫秒）。</summary>
    public long? EndedAt { get; set; }
}
