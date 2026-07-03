using System.Text;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Handoff;
using Netor.Cortana.AI.Hitl;
using Netor.Cortana.AI.MeetingMode.Hitl;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.AI.MeetingMode.Tools;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using MeetingParticipant = Netor.Cortana.Entitys.MeetingParticipantDto;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议模式智能体构建器，负责主持人与参会者 Agent 的装配。
/// </summary>
public sealed class MeetingAgentBuilder
{
    private const int MaxOpeningAttachmentItems = 10;
    private const string HostAgentId = "system-meeting-host";
    private const string SelectorAgentId = "system-meeting-selector";

    /// <summary>会议参会者允许注入的会议专用工具名。</summary>
    internal static IReadOnlyList<string> ParticipantMeetingToolNames { get; } =
        ["list_meeting_attachments"];

    private readonly AIAgentFactory _factory;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingMessageService _messages;
    private readonly MeetingPendingInputService _pending;
    private readonly MeetingAttachmentService _attachments;
    private readonly IAppPaths _appPaths;
    private readonly IPublisher _publisher;
    private readonly IPromptProvider _promptProvider;
    private readonly WorkHandoffTools _handoffTools;
    private readonly MeetingHitlNotifier _hitlNotifier;
    private readonly RetryingFunctionWrapper _retryingWrapper;

    public MeetingAgentBuilder(
        AIAgentFactory factory,
        AgentService agentService,
        AiProviderService providerService,
        AiModelService modelService,
        MeetingSessionService sessions,
        MeetingMessageService messages,
        MeetingPendingInputService pending,
        MeetingAttachmentService attachments,
        IAppPaths appPaths,
        IPublisher publisher,
        IPromptProvider promptProvider,
        WorkHandoffTools handoffTools)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
        _handoffTools = handoffTools ?? throw new ArgumentNullException(nameof(handoffTools));
        _hitlNotifier = new MeetingHitlNotifier(_publisher);
        _retryingWrapper = new RetryingFunctionWrapper();
    }

    /// <summary>构建会议主持人 Agent，注入会议控制工具与 ask_user。</summary>
    public async Task<AIAgent> BuildHostAgentAsync(
        IMeetingTerminationSignal terminationSignal,
        IReadOnlyList<MeetingParticipant> participants,
        string meetingId,
        AiProviderEntity fallbackProvider,
        AiModelEntity fallbackModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminationSignal);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(fallbackProvider);
        ArgumentNullException.ThrowIfNull(fallbackModel);

        var prompt = await _promptProvider.GetPromptAsync("meeting.host", cancellationToken).ConfigureAwait(false)
            ?? "你是会议主持人。请组织参会者围绕主题讨论，并在议题成熟后总结和征询用户是否结束。";
        prompt = prompt.Replace("{participants}", BuildParticipantsBlock(participants), StringComparison.Ordinal);

        var hostAgent = new AgentEntity
        {
            Id = HostAgentId,
            Name = "主持人",
            Description = "会议模式系统主持人",
            Instructions = prompt,
            IsEnabled = true,
        };

        var controlTools = CreateMeetingControlTools(terminationSignal, meetingId);
        var hitlTools = new HitlTools(new MeetingHitlContext(_sessions, meetingId), _hitlNotifier);

        var hostTools = _retryingWrapper.WrapAll([
            hitlTools.CreateAskUserTool(),
            controlTools.CreateOutputSummaryTool(),
            controlTools.CreateCheckPendingUserInputTool(),
            controlTools.CreateConfirmMeetingEndTool(),
            controlTools.CreateListAttachmentsTool(),
            _handoffTools.CreateStartWorkTaskTool(
                "meeting",
                () =>
                {
                    var session = _sessions.GetById(meetingId);
                    return new HandoffRuntimeContext(
                        session?.SessionId,
                        session?.WorkspaceId,
                        null,
                        fallbackProvider.Id,
                        fallbackModel.Id);
                },
                () => BuildDefaultWorkMentions(participants),
                meetingId)
        ]);

        return _factory.Build(
            hostAgent,
            fallbackProvider,
            fallbackModel,
            hostTools,
            ToolFilterMode.MeetingHostExecution);
    }

    /// <summary>构建会议内部调度 Agent。该 Agent 只输出结构化调度 JSON，不参与会议发言，也不拥有任何工具。</summary>
    public AIAgent BuildSelectorAgent(
        AiProviderEntity fallbackProvider,
        AiModelEntity fallbackModel)
    {
        ArgumentNullException.ThrowIfNull(fallbackProvider);
        ArgumentNullException.ThrowIfNull(fallbackModel);

        var selectorAgent = new AgentEntity
        {
            Id = SelectorAgentId,
            Name = "会议调度器",
            Description = "会议模式内部调度器",
            Instructions = "你是会议模式内部调度器。你只根据系统消息输出一个合法 JSON 对象，用于选择下一位发言者；不要写会议正文，不要总结，不要解释。",
            IsEnabled = true,
        };

        return _factory.Build(
            selectorAgent,
            fallbackProvider,
            fallbackModel,
            additionalTools: null,
            ToolFilterMode.None);
    }

    /// <summary>构建会议参会者 Agent，保留原角色设定并追加会议模式上下文。</summary>
    public async Task<AIAgent> BuildParticipantAgentAsync(
        MeetingParticipant participant,
        string meetingId,
        AiProviderEntity fallbackProvider,
        AiModelEntity fallbackModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(fallbackProvider);
        ArgumentNullException.ThrowIfNull(fallbackModel);

        var source = _agentService.GetByName(participant.AgentId)
            ?? throw new InvalidOperationException($"会议参会智能体不存在：{participant.AgentId}");

        var (provider, model) = AIAgentFactory.ResolveSubAgentProviderAndModel(
            source,
            fallbackProvider,
            fallbackModel,
            _providerService,
            _modelService);

        if (provider is null || model is null)
        {
            throw new InvalidOperationException($"会议参会智能体缺少有效厂商或模型：{source.Name}");
        }

        var header = await _promptProvider.GetPromptAsync("meeting.participant_header", cancellationToken).ConfigureAwait(false)
            ?? "【当前模式：会议模式】你正在参加多人会议。主持人安排你完成调研、搜索、数据获取或文档写入时，可以使用你已启用的工具。";

        var cloned = CloneParticipantForMeeting(source, header);
        var controlTools = CreateMeetingControlTools(NoopTerminationSignal.Instance, meetingId);
        var participantTools = _retryingWrapper.WrapAll(CreateParticipantMeetingTools(controlTools));

        return _factory.Build(
            cloned,
            provider,
            model,
            participantTools,
            ToolFilterMode.Full);
    }

    /// <summary>根据会议快照批量构建参会者 Agent。</summary>
    public async Task<IReadOnlyList<AIAgent>> BuildParticipantAgentsAsync(
        IReadOnlyList<MeetingParticipant> participants,
        string meetingId,
        AiProviderEntity fallbackProvider,
        AiModelEntity fallbackModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var result = new List<AIAgent>(participants.Count);
        foreach (var participant in participants.OrderBy(p => p.JoinOrder))
        {
            result.Add(await BuildParticipantAgentAsync(
                    participant,
                    meetingId,
                    fallbackProvider,
                    fallbackModel,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>从 MeetingSessions.ParticipantsJson 读取参会者快照。</summary>
    public static IReadOnlyList<MeetingParticipant> ParseParticipants(MeetingSessionEntity session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.ParticipantsJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize(
                session.ParticipantsJson,
                MeetingJsonContext.Default.ListMeetingParticipantDto)
            ?? [];
    }

    /// <summary>构建会议第一条 user 消息，包含主题、参会者和附件清单。</summary>
    public ChatMessage BuildOpeningMessage(
        MeetingSessionEntity session,
        IReadOnlyList<MeetingParticipant> participants,
        IReadOnlyList<MeetingAttachmentEntity>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(participants);

        attachments ??= _attachments.GetByMeetingId(session.Id);

        var text = BuildOpeningMessageText(
            session,
            participants,
            attachments,
            _appPaths.WorkspaceResourcesDirectory);

        return new ChatMessage(ChatRole.User, text)
        {
            AuthorName = "用户"
        };
    }

    public static string BuildOpeningMessageText(
        MeetingSessionEntity session,
        IReadOnlyList<MeetingParticipant> participants,
        IReadOnlyList<MeetingAttachmentEntity> attachments,
        string workspaceResourcesDirectory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceResourcesDirectory);

        var sb = new StringBuilder();
        sb.AppendLine("# 会议主题");
        sb.AppendLine(string.IsNullOrWhiteSpace(session.Topic) ? "（未填写主题）" : session.Topic.Trim());
        sb.AppendLine();
        sb.AppendLine("# 参会者");

        foreach (var participant in participants.OrderBy(p => p.JoinOrder))
        {
            sb.AppendLine($"- {participant.Name} ({participant.AgentId})");
        }

        if (attachments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 附件");

            foreach (var attachment in attachments.Take(MaxOpeningAttachmentItems))
            {
                var absolutePath = Path.Combine(workspaceResourcesDirectory, attachment.StoredPath);
                sb.AppendLine($"- {attachment.FileName} ({absolutePath}, {attachment.MimeType}, {attachment.SizeBytes} bytes)");
            }

            if (attachments.Count > MaxOpeningAttachmentItems)
            {
                sb.AppendLine($"- ... 等共 {attachments.Count} 个文件，可使用 list_meeting_attachments 查询完整清单。");
            }
        }

        return sb.ToString().Trim();
    }

    private MeetingControlTools CreateMeetingControlTools(
        IMeetingTerminationSignal terminationSignal,
        string meetingId)
    {
        return new MeetingControlTools(
            terminationSignal,
            _sessions,
            _messages,
            _pending,
            _attachments,
            _appPaths,
            _publisher,
            meetingId);
    }

    /// <summary>创建会议参会者可用的会议专用工具。</summary>
    internal static IReadOnlyList<AIFunction> CreateParticipantMeetingTools(MeetingControlTools controlTools)
    {
        ArgumentNullException.ThrowIfNull(controlTools);

        return ParticipantMeetingToolNames
            .Select(name => name switch
            {
                "list_meeting_attachments" => controlTools.CreateListAttachmentsTool(),
                _ => throw new InvalidOperationException($"未知的会议参会者工具：{name}"),
            })
            .ToArray();
    }

    private List<AgentMention> BuildDefaultWorkMentions(IReadOnlyList<MeetingParticipant> participants)
    {
        return participants
            .OrderBy(static p => p.JoinOrder)
            .Select(p => _agentService.GetByName(p.AgentId))
            .Where(static agent => agent is { IsEnabled: true })
            .Select(static agent => new AgentMention(agent!, -1, -1))
            .DistinctBy(static mention => mention.Agent.Id)
            .ToList();
    }

    /// <summary>克隆参会者 Agent，并在原指令前注入会议模式上下文。</summary>
    internal static AgentEntity CloneParticipantForMeeting(AgentEntity source, string header)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(header);

        return new AgentEntity
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Avatar = source.Avatar,
            Instructions = $"{header.Trim()}\n\n---\n\n{source.Instructions}",
            IsEnabled = source.IsEnabled,
            SortOrder = source.SortOrder,
            BoundPlugins = [.. source.BoundPlugins],
            BoundMcp = [.. source.BoundMcp],
            AllowWorkflowMemory = source.AllowWorkflowMemory,
        };
    }

    private static string BuildParticipantsBlock(IReadOnlyList<MeetingParticipant> participants)
    {
        if (participants.Count == 0)
        {
            return "（暂无参会者）";
        }

        var sb = new StringBuilder();
        foreach (var participant in participants.OrderBy(p => p.JoinOrder))
        {
            sb.AppendLine($"- {participant.Name}: {participant.AgentId}");
        }

        return sb.ToString().TrimEnd();
    }

    private sealed class NoopTerminationSignal : IMeetingTerminationSignal
    {
        public static NoopTerminationSignal Instance { get; } = new();

        public void SetTerminationFlag()
        {
        }
    }
}
