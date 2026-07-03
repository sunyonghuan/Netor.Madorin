using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using MeetingParticipant = Netor.Cortana.Entitys.MeetingParticipantDto;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议结束归档服务，将最终总结写回普通对话历史。
/// </summary>
public sealed class MeetingCompletionArchiveService(
    MeetingSessionService sessions,
    CortanaDbContext dbContext,
    ISubscriber subscriber,
    ILogger<MeetingCompletionArchiveService> logger) : IHostedService
{
    /// <summary>启动事件订阅。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        subscriber.Subscribe<MeetingCompletedArgs>(Events.OnMeetingCompleted, (_, args) =>
        {
            Archive(args);
            return Task.FromResult(false);
        });

        return Task.CompletedTask;
    }

    /// <summary>停止事件订阅。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal string Archive(MeetingCompletedArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.MeetingId);

        var meeting = sessions.GetById(args.MeetingId)
            ?? throw new InvalidOperationException($"会议不存在：{args.MeetingId}");
        var finalSummary = !string.IsNullOrWhiteSpace(meeting.FinalSummaryMd)
            ? meeting.FinalSummaryMd
            : args.FinalSummary;

        if (string.IsNullOrWhiteSpace(finalSummary))
        {
            throw new InvalidOperationException($"会议 {args.MeetingId} 没有可归档的最终总结。");
        }

        var content = BuildArchiveContent(meeting, finalSummary);
        var contents = new List<AIContent> { new TextContent(content) };
        var now = DateTimeOffset.Now;
        var timestamp = now.ToUnixTimeMilliseconds();
        var message = new ChatMessageEntity
        {
            Id = BuildArchiveMessageId(args.MeetingId),
            SessionId = meeting.SessionId,
            Role = ChatRole.Assistant.ToString(),
            AuthorName = "会议主持人",
            Content = ChatMessageExtensions.BuildPersistedContent(content, contents),
            ContentsJson = ChatMessageExtensions.BuildContentsJson(contents),
            CreatedAt = now,
            CreatedTimestamp = timestamp,
            UpdatedTimestamp = timestamp,
            ModelName = "meeting.archive",
            AgentId = meeting.HostAgentId,
            AgentName = "会议主持人",
        };

        dbContext.ExecuteInTransaction(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO ChatMessages
                    (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName)
                VALUES
                    (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt, @AgentId, @AgentName)
                """;
            ChatMessageService.BindEntity(cmd, message);
            cmd.ExecuteNonQuery();

            using var archiveSessionCmd = conn.CreateCommand();
            archiveSessionCmd.CommandText = """
                UPDATE ChatSessions
                SET IsArchived = 1, UpdatedTimestamp = @UpdatedTimestamp
                WHERE Id = @SessionId
                """;
            archiveSessionCmd.Parameters.AddWithValue("@UpdatedTimestamp", timestamp);
            archiveSessionCmd.Parameters.AddWithValue("@SessionId", meeting.SessionId);
            archiveSessionCmd.ExecuteNonQuery();
        });

        logger.LogInformation(
            "会议结束总结已归档到 ChatMessages：meetingId={MeetingId}, sessionId={SessionId}, messageId={MessageId}",
            args.MeetingId,
            meeting.SessionId,
            message.Id);

        return message.Id;
    }

    internal static string BuildArchiveContent(MeetingSessionEntity meeting, string finalSummary)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalSummary);

        var participants = MeetingAgentBuilder.ParseParticipants(meeting);
        var sb = new StringBuilder();
        sb.AppendLine("# 会议总结");
        sb.AppendLine();
        sb.AppendLine($"- 会议ID：{meeting.Id}");
        sb.AppendLine($"- 主题：{(string.IsNullOrWhiteSpace(meeting.Topic) ? "（未填写主题）" : meeting.Topic.Trim())}");
        sb.AppendLine($"- 参会者：{BuildParticipantNames(participants)}");
        sb.AppendLine();
        sb.AppendLine(finalSummary.Trim());
        return sb.ToString().Trim();
    }

    private static string BuildArchiveMessageId(string meetingId) => $"meeting_archive_{meetingId}";

    private static string BuildParticipantNames(IReadOnlyList<MeetingParticipant> participants)
    {
        if (participants.Count == 0)
        {
            return "（无参会者快照）";
        }

        return string.Join("、", participants.OrderBy(p => p.JoinOrder).Select(p => p.Name));
    }
}
