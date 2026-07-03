using System.Collections.Concurrent;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议模式执行器，负责装配 GroupChat 工作流并管理恢复、插话与取消。
/// </summary>
public sealed class MeetingExecutor
{
    private const int MinParticipants = 2;

    private readonly MeetingAgentBuilder _agentBuilder;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingMessageService _messages;
    private readonly MeetingPendingInputService _pendingInputs;
    private readonly MeetingAttachmentService _attachments;
    private readonly MeetingHistoryProvider _historyProvider;
    private readonly MeetingCompactionService _compaction;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly MeetingCancellationRegistry _cancellationRegistry;
    private readonly IPublisher _publisher;
    private readonly ILogger<MeetingExecutor> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, MeetingHostManager> _runningManagers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pauseRequests = new(StringComparer.OrdinalIgnoreCase);

    public MeetingExecutor(
        MeetingAgentBuilder agentBuilder,
        MeetingSessionService sessions,
        MeetingMessageService messages,
        MeetingPendingInputService pendingInputs,
        MeetingAttachmentService attachments,
        MeetingHistoryProvider historyProvider,
        MeetingCompactionService compaction,
        AiProviderService providerService,
        AiModelService modelService,
        MeetingCancellationRegistry cancellationRegistry,
        IPublisher publisher,
        ILogger<MeetingExecutor> logger,
        ILoggerFactory loggerFactory)
    {
        _agentBuilder = agentBuilder ?? throw new ArgumentNullException(nameof(agentBuilder));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _pendingInputs = pendingInputs ?? throw new ArgumentNullException(nameof(pendingInputs));
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _historyProvider = historyProvider ?? throw new ArgumentNullException(nameof(historyProvider));
        _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>启动并运行指定会议。</summary>
    public async Task ExecuteAsync(
        string meetingId,
        string? fallbackProviderId = null,
        string? fallbackModelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        using var nonExpertScope = NonExpertProcessScope.Enter();

        var session = _sessions.GetById(meetingId)
            ?? throw new InvalidOperationException($"会议不存在：{meetingId}");
        var providerId = string.IsNullOrWhiteSpace(session.Provider) ? fallbackProviderId : session.Provider;
        var modelId = string.IsNullOrWhiteSpace(session.Model) ? fallbackModelId : session.Model;
        var provider = !string.IsNullOrWhiteSpace(providerId)
            ? _providerService.GetById(providerId)
            : null;
        var model = !string.IsNullOrWhiteSpace(modelId)
            ? _modelService.GetById(modelId)
            : null;

        if (provider is null || model is null)
        {
            throw new InvalidOperationException($"会议 {meetingId} 缺少有效 AI 厂商或模型。");
        }

        var participants = MeetingAgentBuilder.ParseParticipants(session);
        if (participants.Count < MinParticipants)
        {
            _sessions.MarkAsAdjourned(meetingId);
            await _publisher.PublishAsync(
                Events.OnMeetingCancelled,
                new MeetingCancelledArgs(meetingId, "participant_count_less_than_2"));
            throw new InvalidOperationException($"会议至少需要 {MinParticipants} 位参会智能体。");
        }

        _logger.LogInformation(
            "会议 {MeetingId} 开始执行：Provider={ProviderName}({ProviderId})，Model={ModelName}({ModelId})，Participants={ParticipantCount}",
            meetingId,
            provider.Name,
            provider.Id,
            model.Name,
            model.Id,
            participants.Count);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_cancellationRegistry.TryRegister(meetingId, linkedCts))
        {
            throw new InvalidOperationException($"会议 {meetingId} 已有运行中的执行轮次。");
        }

        try
        {
            var participantAgents = await _agentBuilder.BuildParticipantAgentsAsync(
                    participants,
                    meetingId,
                    provider,
                    model,
                    linkedCts.Token)
                .ConfigureAwait(false);
            _logger.LogInformation("会议 {MeetingId} 参会者 Agent 构建完成：{AgentCount}", meetingId, participantAgents.Count);

            var manager = new MeetingHostManager(
                participantAgents,
                meetingId,
                _sessions,
                _pendingInputs,
                _messages,
                _historyProvider,
                _publisher,
                _loggerFactory.CreateLogger<MeetingHostManager>());

            var hostAgent = await _agentBuilder.BuildHostAgentAsync(
                    manager,
                    participants,
                    meetingId,
                    provider,
                    model,
                    linkedCts.Token)
                .ConfigureAwait(false);
            manager.SetHostAgent(hostAgent);
            _logger.LogInformation("会议 {MeetingId} 主持人 Agent 构建完成：{HostAgentId}", meetingId, hostAgent.Id);

            var selectorAgent = _agentBuilder.BuildSelectorAgent(provider, model);
            manager.SetSelectorAgent(selectorAgent);
            _logger.LogInformation("会议 {MeetingId} 内部调度 Agent 构建完成：{SelectorAgentId}", meetingId, selectorAgent.Id);

            _runningManagers[meetingId] = manager;
            var workflowParticipants = participantAgents
                .Prepend(hostAgent)
                .ToArray();

            var shouldSendOpeningMessage = _messages.CountByMeeting(meetingId) == 0;
            var openingMessage = _agentBuilder.BuildOpeningMessage(
                session,
                participants,
                _attachments.GetByMeetingId(meetingId));
            if (shouldSendOpeningMessage)
            {
                AppendUserMessage(meetingId, openingMessage.Text);
            }

            var workflow = AgentWorkflowBuilder
                .CreateGroupChatBuilderWith(_ => manager)
                .AddParticipants(workflowParticipants)
                .WithName($"meeting-{meetingId}")
                .Build();

            var processor = new MeetingStreamProcessor(
                meetingId,
                workflowParticipants,
                _sessions,
                _messages,
                _compaction,
                _publisher,
                _loggerFactory.CreateLogger<MeetingStreamProcessor>());

            _logger.LogInformation("会议 {MeetingId} 启动 GroupChat 流式执行", meetingId);
            await using var run = await InProcessExecution.OpenStreamingAsync(
                    workflow,
                    sessionId: meetingId,
                    linkedCts.Token)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "会议 {MeetingId} GroupChat 已启动，发送 TurnToken。ShouldSendOpeningMessage={ShouldSendOpeningMessage}",
                meetingId,
                shouldSendOpeningMessage);
            var openingSent = !shouldSendOpeningMessage ||
                await run.TrySendMessageAsync(openingMessage).ConfigureAwait(false);
            var turnTokenSent = await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);
            if (!openingSent || !turnTokenSent)
            {
                throw new InvalidOperationException(
                    $"会议 {meetingId} 无法向 GroupChat 工作流发送开场消息或 TurnToken。OpeningSent={openingSent}, TurnTokenSent={turnTokenSent}");
            }

            _logger.LogInformation("会议 {MeetingId} GroupChat 首轮消息已发送，开始监听 Workflow 事件", meetingId);

            try
            {
                await foreach (var workflowEvent in run.WatchStreamAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "会议 {MeetingId} 收到 Workflow 事件：{EventType}",
                        meetingId,
                        workflowEvent.GetType().Name);
                    await processor.ProcessEventAsync(workflowEvent, linkedCts.Token).ConfigureAwait(false);
                    if (IsTerminalWorkflowOutput(workflowEvent))
                    {
                        _logger.LogInformation("会议 {MeetingId} 收到终止 WorkflowOutputEvent，结束监听", meetingId);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await processor.SavePartialsAsync(ex, linkedCts.Token).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            if (_pauseRequests.TryRemove(meetingId, out _))
            {
                _logger.LogInformation("会议 {MeetingId} 当前执行轮次已暂停，会议保持可继续状态", meetingId);
                await _publisher.PublishAsync(
                    Events.OnMeetingPaused,
                    new MeetingPausedArgs(meetingId, "user_pause"));
            }
            else
            {
                _logger.LogInformation("会议 {MeetingId} 被取消", meetingId);
                _sessions.MarkAsAdjourned(meetingId);
                await _publisher.PublishAsync(
                    Events.OnMeetingCancelled,
                    new MeetingCancelledArgs(meetingId, "user_cancel"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会议 {MeetingId} 执行失败", meetingId);
            _sessions.MarkAsAdjourned(meetingId);
            await _publisher.PublishAsync(
                Events.OnMeetingCancelled,
                new MeetingCancelledArgs(meetingId, ex.Message));
            throw;
        }
        finally
        {
            _runningManagers.TryRemove(meetingId, out _);
            _cancellationRegistry.TryUnregister(meetingId);
        }
    }

    internal static bool IsTerminalWorkflowOutput(WorkflowEvent workflowEvent)
    {
        return workflowEvent is WorkflowOutputEvent
            and not AgentResponseUpdateEvent
            and not AgentResponseEvent;
    }

    /// <summary>判断会议当前是否有正在运行的执行轮次。</summary>
    public bool IsRunning(string meetingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        return _runningManagers.ContainsKey(meetingId);
    }

    /// <summary>恢复主持人 ask_user 挂起流程。</summary>
    public async Task ResumeAsync(
        string meetingId,
        string userReply,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        if (string.IsNullOrWhiteSpace(userReply))
        {
            return;
        }

        var session = _sessions.GetById(meetingId);
        if (string.IsNullOrWhiteSpace(session?.PendingRequestId))
        {
            _logger.LogWarning("会议 {MeetingId} 没有挂起请求，忽略重复 Resume", meetingId);
            return;
        }

        var rows = _sessions.TryClearPending(meetingId, session.PendingRequestId);
        if (rows == 0)
        {
            _logger.LogWarning("会议 {MeetingId} Resume 乐观锁失败，忽略重复回复", meetingId);
            return;
        }

        var message = AppendUserMessage(meetingId, userReply.Trim());
        await _publisher.PublishAsync(
            Events.OnMeetingUserSpoke,
            new MeetingUserSpokeArgs(meetingId, message.Id, message.ContentMd, "reply"));

        if (_runningManagers.TryGetValue(meetingId, out var manager))
        {
            manager.OnUserReplied();
        }
    }

    /// <summary>用户主动插话，下一轮由 MeetingHostManager 注入历史。</summary>
    public async Task EnqueueInterruptAsync(
        string meetingId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var content = text.Trim();
        var message = AppendUserMessage(meetingId, content, "interrupt");
        _pendingInputs.Enqueue(meetingId, content, kind: "interrupt");
        await _publisher.PublishAsync(
            Events.OnMeetingUserSpoke,
            new MeetingUserSpokeArgs(meetingId, message.Id, message.ContentMd, "interrupt"));
    }

    /// <summary>取消当前会议执行轮次。</summary>
    public async Task CancelAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        _pauseRequests.TryRemove(meetingId, out _);
        var cancellationRequested = await _cancellationRegistry.CancelAsync(meetingId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationRequested)
        {
            return;
        }

        _sessions.MarkAsAdjourned(meetingId);
        await _publisher.PublishAsync(
            Events.OnMeetingCancelled,
            new MeetingCancelledArgs(meetingId, "user_cancel"));
    }

    /// <summary>暂停当前执行轮次但保留会议，允许老板继续输入总结或讨论指令。</summary>
    public async Task PauseAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        _pauseRequests[meetingId] = 1;
        var cancellationRequested = await _cancellationRegistry.CancelAsync(meetingId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationRequested)
        {
            return;
        }

        _pauseRequests.TryRemove(meetingId, out _);
        await _publisher.PublishAsync(
            Events.OnMeetingPaused,
            new MeetingPausedArgs(meetingId, "user_pause"));
    }

    private MeetingMessageEntity AppendUserMessage(
        string meetingId,
        string content,
        string messageRole = "normal")
    {
        var message = new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "user",
            SpeakerId = "user",
            SpeakerName = "老板",
            ContentMd = content,
            MessageRole = messageRole,
        };

        _messages.Append(message);
        return message;
    }
}
