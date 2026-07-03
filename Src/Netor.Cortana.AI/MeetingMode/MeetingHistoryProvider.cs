using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 从会议持久化数据重建 LLM 可用上下文。
/// </summary>
public sealed class MeetingHistoryProvider
{
    private const int DefaultMaxDisplaySegments = 10;
    private const string OpeningMessagePrefix = "# 会议主题";

    private readonly MeetingMessageService _messages;
    private readonly MeetingCompactionSegmentService _segments;
    private readonly SystemSettingsService _settings;
    private readonly CortanaDbContext _db;

    public MeetingHistoryProvider(
        MeetingMessageService messages,
        MeetingCompactionSegmentService segments,
        SystemSettingsService settings,
        CortanaDbContext db)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>构建会议 LLM 历史，过滤流式异常留下的部分消息。</summary>
    public Task<List<ChatMessage>> BuildLlmHistoryAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = _db.ExecuteInDeferredTransaction((connection, transaction) =>
        {
            var segments = _segments.GetByMeetingId(connection, transaction, meetingId);
            return BuildFromSnapshot(connection, transaction, meetingId, segments);
        });

        return Task.FromResult(result);
    }

    /// <summary>把用户插话队列转换成 ChatMessage，并标记为已消费。</summary>
    public List<ChatMessage> DrainPendingInputsAsUserMessages(string meetingId, MeetingPendingInputService pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var persistedUserMessages = _messages.ListCompleteByMeeting(meetingId)
            .Where(static message => string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase))
            .Select(static message => message.ContentMd)
            .ToHashSet(StringComparer.Ordinal);

        return pending.Drain(meetingId)
            .Where(input => !persistedUserMessages.Contains(input.Content))
            .Select(input => new ChatMessage(ChatRole.User, input.Content)
            {
                AuthorName = "用户"
            })
            .ToList();
    }

    private List<ChatMessage> BuildFromSnapshot(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string meetingId,
        IReadOnlyList<MeetingCompactionSegmentEntity> segments)
    {
        var result = new List<ChatMessage>();

        if (segments.Count > 0)
        {
            var maxDisplay = _settings.GetValue("Meeting.Compaction.MaxDisplaySegments", DefaultMaxDisplaySegments);
            var displaySegments = segments.Count > maxDisplay
                ? segments.Skip(segments.Count - maxDisplay).ToList()
                : segments;

            foreach (var segment in displaySegments)
            {
                result.Add(new ChatMessage(
                    ChatRole.System,
                    $"[会议摘要 #{segment.SegmentIndex + 1}]\n{segment.Summary}"));
            }

            var tailStart = segments[^1].EndSequence + 1;
            result.AddRange(ConvertMessages(_messages.ListCompleteBySequence(
                connection,
                transaction,
                meetingId,
                tailStart)));
            return result;
        }

        result.AddRange(ConvertMessages(_messages.ListCompleteByMeeting(
            connection,
            transaction,
            meetingId)));
        return result;
    }

    private static IEnumerable<ChatMessage> ConvertMessages(IEnumerable<MeetingMessageEntity> messages)
    {
        foreach (var message in messages)
        {
            var isOpeningContext = string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase) &&
                message.Sequence == 0 &&
                message.ContentMd.TrimStart().StartsWith(OpeningMessagePrefix, StringComparison.Ordinal);
            var role = isOpeningContext
                ? ChatRole.System
                : string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User
                : ChatRole.Assistant;

            yield return new ChatMessage(role, BuildMessageContentForLlm(message, isOpeningContext))
            {
                AuthorName = isOpeningContext ? "会议上下文" : ResolveAuthorName(message)
            };
        }
    }

    private static string BuildMessageContentForLlm(MeetingMessageEntity message, bool isOpeningContext)
    {
        if (isOpeningContext)
        {
            return message.ContentMd;
        }

        if (string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(message.MessageRole, "interrupt", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
            [老板插话/流程变更事件]
            {message.ContentMd}

            处理要求：主持人必须先判断这条插话是补充、纠错、范围变化、优先级变化、结束请求还是无关信息；只能局部修正受影响阶段，不能从零重新开会。
            """;
        }

        return message.ContentMd;
    }

    private static string ResolveAuthorName(MeetingMessageEntity message)
    {
        if (string.Equals(message.SpeakerKind, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "主持人";
        }

        if (string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase))
        {
            return "用户";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerName)
            ? message.SpeakerId
            : message.SpeakerName;
    }
}
