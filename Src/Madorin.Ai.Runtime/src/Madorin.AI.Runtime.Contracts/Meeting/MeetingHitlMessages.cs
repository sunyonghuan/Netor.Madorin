namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Meeting-specific human-in-the-loop request.
/// Does not require <c>toolId</c> or <c>callId</c>.
/// </summary>
public sealed record MeetingHitlRequest(
    string ApprovalRequestId,
    string SessionId,
    string RunId,
    int RoundIndex,
    string HostInvocationId,
    string Prompt,
    ContentBlock[]? Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>User decision for a meeting HITL request.</summary>
public sealed record MeetingHitlResponse(
    string ApprovalRequestId,
    MeetingHitlAction Action,
    string? Supplement = null,
    string? Reason = null,
    DateTimeOffset? RespondedAt = null);
