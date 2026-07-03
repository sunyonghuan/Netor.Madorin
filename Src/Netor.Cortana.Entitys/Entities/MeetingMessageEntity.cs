namespace Netor.Cortana.Entitys;

/// <summary>
/// 会议模式消息实体（MeetingMessages 表）。
/// </summary>
public sealed class MeetingMessageEntity
{
    /// <summary>消息 ID（GUID(N)）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的会议 ID。</summary>
    public string MeetingId { get; set; } = string.Empty;

    /// <summary>会议内单调递增序号。</summary>
    public int Sequence { get; set; }

    /// <summary>发言者类型：agent / host / user。</summary>
    public string SpeakerKind { get; set; } = string.Empty;

    /// <summary>发言者 ID。</summary>
    public string SpeakerId { get; set; } = string.Empty;

    /// <summary>发言者显示名快照。</summary>
    public string SpeakerName { get; set; } = string.Empty;

    /// <summary>正文 Markdown。</summary>
    public string ContentMd { get; set; } = string.Empty;

    /// <summary>思考过程 Markdown。</summary>
    public string? ThinkingMd { get; set; }

    /// <summary>工具调用 JSON。</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>附件引用 JSON。</summary>
    public string? AttachmentsJson { get; set; }

    /// <summary>消息角色：normal / summary / inquiry。</summary>
    public string MessageRole { get; set; } = "normal";

    /// <summary>是否等待用户回复。</summary>
    public bool AwaitingUserReply { get; set; }

    /// <summary>是否为流式异常时保存的部分消息。</summary>
    public bool IsPartial { get; set; }

    /// <summary>部分消息的错误描述。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
