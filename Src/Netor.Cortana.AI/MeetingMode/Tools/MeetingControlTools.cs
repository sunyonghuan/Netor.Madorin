using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode.Tools;

/// <summary>
/// 会议主持人与参会者使用的会议控制工具。
/// </summary>
public sealed class MeetingControlTools
{
    private readonly IMeetingTerminationSignal _terminationSignal;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingMessageService _messageService;
    private readonly MeetingPendingInputService _pendingService;
    private readonly MeetingAttachmentService _attachmentService;
    private readonly IAppPaths _appPaths;
    private readonly IPublisher _publisher;
    private readonly string _meetingId;

    public MeetingControlTools(
        IMeetingTerminationSignal terminationSignal,
        MeetingSessionService sessions,
        MeetingMessageService messageService,
        MeetingPendingInputService pendingService,
        MeetingAttachmentService attachmentService,
        IAppPaths appPaths,
        IPublisher publisher,
        string meetingId)
    {
        _terminationSignal = terminationSignal ?? throw new ArgumentNullException(nameof(terminationSignal));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _messageService = messageService ?? throw new ArgumentNullException(nameof(messageService));
        _pendingService = pendingService ?? throw new ArgumentNullException(nameof(pendingService));
        _attachmentService = attachmentService ?? throw new ArgumentNullException(nameof(attachmentService));
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _meetingId = meetingId ?? throw new ArgumentNullException(nameof(meetingId));
    }

    /// <summary>创建主持人输出会议总结工具。</summary>
    public AIFunction CreateOutputSummaryTool()
    {
        [Description("当你要输出会议总结时调用。总结会作为单独的 summary 消息写入会议记录。")]
        async Task<string> OutputSummaryAsync(
            [Description("会议总结的完整 markdown 内容，覆盖每位参会者关键观点、共识、待办事项")]
            string content,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "错误：总结内容不能为空";
            }

            var summary = content.Trim();
            if (LooksLikeInternalSelectorPayload(summary))
            {
                return "错误：output_summary 收到的是内部调度 JSON，不是会议总结；已拒绝写入会议记录。";
            }

            var message = await _messageService.AppendSummaryAsync(_meetingId, summary, ct);
            await _publisher.PublishAsync(
                Events.OnMeetingSummaryDraft,
                new MeetingSummaryDraftArgs(_meetingId, summary));
            await _publisher.PublishAsync(
                Events.OnMeetingMessageCompleted,
                CreateMeetingMessageCompletedArgs(message, summary));

            return "会议总结已记录。下一步必须先调用 check_pending_user_input；如果没有未处理插话，必须调用 ask_user 征询老板是否可以结束。不要再选择参会者继续讨论原始话题。";
        }

        return AIFunctionFactory.Create(OutputSummaryAsync, new AIFunctionFactoryOptions
        {
            Name = "output_summary",
            Description = "输出会议总结。"
        });
    }

    private MeetingMessageCompletedArgs CreateMeetingMessageCompletedArgs(MeetingMessageEntity message, string summary)
    {
        var session = _sessions.GetById(_meetingId);
        return new MeetingMessageCompletedArgs(
            MeetingId: _meetingId,
            MessageId: message.Id,
            SpeakerId: message.SpeakerId,
            SpeakerKind: message.SpeakerKind,
            ContentMd: summary,
            MessageRole: "summary",
            AwaitingUserReply: false,
            SessionId: session?.SessionId ?? string.Empty,
            WorkspaceId: session?.WorkspaceId ?? string.Empty,
            Topic: session?.Topic ?? string.Empty,
            HostAgentId: session?.HostAgentId ?? string.Empty,
            Model: session?.Model ?? string.Empty,
            CreatedAt: message.CreatedAt,
            SpeakerName: message.SpeakerName);
    }

    /// <summary>识别内部调度器 JSON，避免被误写成会议总结。</summary>
    private static bool LooksLikeInternalSelectorPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var propertyCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                propertyCount++;
                if (IsSelectorDecisionProperty(property.Name))
                {
                    return true;
                }
            }

            return propertyCount == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSelectorDecisionProperty(string name)
    {
        return string.Equals(name, "executionPlan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "currentPhaseId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "nextSpeakerId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "readyForSpeaker", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>创建主持人确认会议结束工具。</summary>
    public AIFunction CreateConfirmMeetingEndTool()
    {
        [Description("当用户已确认同意结束会议时调用，会取最近 summary 写入最终总结并标记终止。")]
        async Task<string> ConfirmMeetingEndAsync(CancellationToken ct)
        {
            var latestSummary = await _messageService.GetLatestSummaryAsync(_meetingId, ct);
            if (latestSummary is null)
            {
                return "错误：没有找到会议总结。请先用 output_summary 工具输出总结再结束。";
            }

            _sessions.SetFinalSummary(_meetingId, latestSummary.ContentMd);
            _terminationSignal.SetTerminationFlag();
            return "会议已正式结束，FinalSummary 已写入。不要再继续讨论、不要重新开始原始话题、不要再选择任何参会者。";
        }

        return AIFunctionFactory.Create(ConfirmMeetingEndAsync, new AIFunctionFactoryOptions
        {
            Name = "confirm_meeting_end",
            Description = "标记会议正式结束。"
        });
    }

    /// <summary>创建用户插话检查工具。</summary>
    public AIFunction CreateCheckPendingUserInputTool()
    {
        [Description("查询当前是否有用户插话尚未消费。调用 ask_user 前必须先检查。")]
        Task<string> CheckPendingAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var pending = _pendingService.PeekUnconsumed(_meetingId);
            if (pending.Count == 0)
            {
                return Task.FromResult("没有未处理的用户插话，可以继续。");
            }

            var summary = string.Join("\n", pending.Select((p, i) =>
                $"{i + 1}. [{DateTimeOffset.FromUnixTimeMilliseconds(p.EnqueuedAt):HH:mm}] {p.Content}"));

            return Task.FromResult(
                $"有 {pending.Count} 条用户插话尚未处理：\n{summary}\n\n请先处理这些插话，而不是征询会议结束。");
        }

        return AIFunctionFactory.Create(CheckPendingAsync, new AIFunctionFactoryOptions
        {
            Name = "check_pending_user_input",
            Description = "检查用户插话队列，在调用 ask_user 前必须先调用。"
        });
    }

    /// <summary>创建会议附件列表工具。</summary>
    public AIFunction CreateListAttachmentsTool()
    {
        [Description("列出当前会议的所有附件清单。查询本身不读取文件内容。")]
        Task<string> ListAttachmentsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var attachments = _attachmentService.GetByMeetingId(_meetingId);
            if (attachments.Count == 0)
            {
                return Task.FromResult("本次会议没有附件。");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"本次会议共 {attachments.Count} 个附件：");
            foreach (var att in attachments)
            {
                var absolutePath = Path.Combine(_appPaths.WorkspaceResourcesDirectory, att.StoredPath);
                sb.AppendLine($"- {att.FileName} (路径：{absolutePath}, 类型：{att.MimeType}, 大小：{att.SizeBytes} 字节)");
            }

            return Task.FromResult(sb.ToString());
        }

        return AIFunctionFactory.Create(ListAttachmentsAsync, new AIFunctionFactoryOptions
        {
            Name = "list_meeting_attachments",
            Description = "列出会议附件清单。"
        });
    }
}
