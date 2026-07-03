using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.Json;

using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.AI.Reliability;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议 GroupChat 管理器，由主持人 Agent 决定下一位发言者并处理 HITL 阻塞。
/// </summary>
public sealed class MeetingHostManager : GroupChatManager, IMeetingTerminationSignal
{
    private const string HostSpeakerId = "HOST";
    private const string HostAgentId = "system-meeting-host";
    private const string OpeningMessagePrefix = "# 会议主题";

    private readonly IReadOnlyList<AIAgent> _participants;
    private readonly string _meetingId;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingPendingInputService _pending;
    private readonly MeetingMessageService _messages;
    private readonly MeetingHistoryProvider _historyProvider;
    private readonly IPublisher _publisher;
    private readonly ILogger<MeetingHostManager> _logger;
    private readonly Dictionary<string, AIAgent> _participantById;

    private AIAgent? _hostAgent;
    private AIAgent? _selectorAgent;
    private TaskCompletionSource<bool>? _userReplyTcs;
    private volatile bool _terminationFlag;
    private string? _lastSpeakerId;

    public MeetingHostManager(
        IReadOnlyList<AIAgent> participants,
        string meetingId,
        MeetingSessionService sessions,
        MeetingPendingInputService pending,
        MeetingMessageService messages,
        MeetingHistoryProvider historyProvider,
        IPublisher publisher,
        ILogger<MeetingHostManager> logger)
    {
        _participants = participants ?? throw new ArgumentNullException(nameof(participants));
        _meetingId = meetingId ?? throw new ArgumentNullException(nameof(meetingId));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _historyProvider = historyProvider ?? throw new ArgumentNullException(nameof(historyProvider));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _participantById = _participants.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        MaximumIterationCount = 40;
    }

    /// <summary>回填主持人 Agent。用于打破主持人工具依赖 HostManager 的装配环。</summary>
    public void SetHostAgent(AIAgent hostAgent)
    {
        _hostAgent = hostAgent ?? throw new ArgumentNullException(nameof(hostAgent));
    }

    /// <summary>回填内部调度 Agent。调度 Agent 不参与会议发言，只负责输出选人 JSON。</summary>
    public void SetSelectorAgent(AIAgent selectorAgent)
    {
        _selectorAgent = selectorAgent ?? throw new ArgumentNullException(nameof(selectorAgent));
    }

    /// <summary>用户回复 HITL 后唤醒下一轮选择。</summary>
    public void OnUserReplied()
    {
        _userReplyTcs?.TrySetResult(true);
    }

    /// <summary>显式标记会议应终止。</summary>
    public void SetTerminationFlag()
    {
        _terminationFlag = true;
        _userReplyTcs?.TrySetResult(true);
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> UpdateHistoryAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var rebuilt = await _historyProvider.BuildLlmHistoryAsync(_meetingId, cancellationToken).ConfigureAwait(false);
        var userMessages = _historyProvider.DrainPendingInputsAsUserMessages(_meetingId, _pending);
        if (userMessages.Count > 0)
        {
            rebuilt.AddRange(userMessages);
        }

        var flow = AnalyzeFlow();
        if (flow.MustSelectHost)
        {
            rebuilt.Add(new ChatMessage(ChatRole.System, BuildHostFlowControlPrompt(flow)));
        }

        return rebuilt;
    }

    protected override async ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var hostAgent = EnsureHostAgent();
        if (HasPendingUserRequest())
        {
            _userReplyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _userReplyTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _userReplyTcs = null;
            }

            if (_terminationFlag)
            {
                return hostAgent;
            }
        }

        var freshHistory = (await UpdateHistoryAsync(history, cancellationToken).ConfigureAwait(false)).ToList();
        var flow = AnalyzeFlow();
        if (flow.MustSelectHost)
        {
            _logger.LogInformation(
                "会议 {MeetingId} 当前处于 {Stage}，由主持人接管：{Reason}",
                _meetingId,
                flow.Stage,
                flow.Reason);
            _lastSpeakerId = HostSpeakerId;
            return hostAgent;
        }

        var selectorAgent = EnsureSelectorAgent();
        var selectorPrompt = BuildSelectorPrompt(freshHistory, flow);
        var decisionMessages = new List<ChatMessage>(freshHistory.Count + 1);
        decisionMessages.AddRange(freshHistory);
        decisionMessages.Add(new ChatMessage(ChatRole.System, selectorPrompt));

        _logger.LogInformation(
            "会议 {MeetingId} 内部调度器开始选择下一位发言者：History={HistoryCount}, Iteration={IterationCount}/{MaximumIterationCount}",
            _meetingId,
            freshHistory.Count,
            IterationCount,
            MaximumIterationCount);

        var response = await InvokeWithLlmRetryAsync(
                () => selectorAgent.RunAsync(decisionMessages, cancellationToken: cancellationToken),
                nameof(SelectNextAgentAsync),
                cancellationToken)
            .ConfigureAwait(false);

        var rawDecision = response.Text ?? string.Empty;
        var decision = TryParseDecision(rawDecision);
        if (decision is null && LooksLikeStructuredDecision(rawDecision))
        {
            _logger.LogError(
                "会议 {MeetingId} 内部调度器输出结构化决策但 JSON 无法解析：{RawDecision}",
                _meetingId,
                TruncateForLog(rawDecision, 240));
            _lastSpeakerId = HostSpeakerId;
            return hostAgent;
        }

        var picked = decision is null
            ? ResolveAgent(rawDecision)
            : ResolveAgent(decision.ReadyForSpeaker ? decision.NextSpeakerId : HostSpeakerId);

        if (decision is not null)
        {
            _logger.LogInformation(
                "会议 {MeetingId} 结构化调度决策：Phase={Phase}, Need={Need}, Next={NextSpeakerId}, Ready={ReadyForSpeaker}, Reason={Reason}",
                _meetingId,
                decision.Phase,
                decision.Need,
                decision.NextSpeakerId,
                decision.ReadyForSpeaker,
                decision.Reason);
        }

        var dispatchPlan = BuildDispatchPlan(flow.Messages, decision);
        picked = ApplyDispatchGuards(picked, decision, dispatchPlan, flow, hostAgent);

        _logger.LogInformation(
            "会议 {MeetingId} 主持人选择完成：Raw={RawDecision}, Picked={PickedAgentId}",
            _meetingId,
            TruncateForLog(rawDecision, 160),
            ReferenceEquals(picked, hostAgent) ? HostSpeakerId : picked.Id);

        _lastSpeakerId = ReferenceEquals(picked, hostAgent) ? HostSpeakerId : picked.Id;
        return picked;
    }

    protected override async ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (IsMeetingCompleted())
        {
            _terminationFlag = true;
            return true;
        }

        if (_terminationFlag)
        {
            return true;
        }

        if (IterationCount < MaximumIterationCount)
        {
            return false;
        }

        _logger.LogWarning("会议 {MeetingId} 达到最大迭代次数 {MaxIterationCount}，强制进入总结结束", _meetingId, MaximumIterationCount);
        await ForceFinalSummaryAsync(cancellationToken).ConfigureAwait(false);
        _terminationFlag = true;
        return true;
    }

    private AIAgent EnsureHostAgent()
    {
        return _hostAgent ?? throw new InvalidOperationException("会议主持人 Agent 尚未设置。请先调用 SetHostAgent。");
    }

    private AIAgent EnsureSelectorAgent()
    {
        return _selectorAgent ?? throw new InvalidOperationException("会议调度 Agent 尚未设置。请先调用 SetSelectorAgent。");
    }

    private bool HasPendingUserRequest()
    {
        var session = _sessions.GetById(_meetingId);
        return !string.IsNullOrWhiteSpace(session?.PendingRequestId);
    }

    private bool IsMeetingCompleted()
    {
        return _sessions.GetById(_meetingId)?.Status == 2;
    }

    private AIAgent ResolveAgent(string raw)
    {
        var hostAgent = EnsureHostAgent();
        var cleaned = CleanDecisionText(raw);

        if (string.Equals(cleaned, HostSpeakerId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleaned, HostAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return hostAgent;
        }

        if (_participantById.TryGetValue(cleaned, out var exact))
        {
            return exact;
        }

        if (cleaned.Contains(HostSpeakerId, StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains(HostAgentId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("主持人输出格式不规范：{Raw}，模糊匹配到 HOST", raw);
            return hostAgent;
        }

        foreach (var participant in _participants)
        {
            if ((!string.IsNullOrWhiteSpace(participant.Id) &&
                    cleaned.Contains(participant.Id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(participant.Name) &&
                    cleaned.Contains(participant.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("主持人输出格式不规范：{Raw}，模糊匹配到 {AgentId}", raw, participant.Id);
                return participant;
            }
        }

        if (_participants.Count > 0)
        {
            _logger.LogError("主持人输出无法解析：{Raw}，兜底选择首位参会者 {AgentId}", raw, _participants[0].Id);
            return _participants[0];
        }

        _logger.LogWarning("会议 {MeetingId} 没有参会者，兜底选择主持人", _meetingId);
        return hostAgent;
    }

    private static string CleanDecisionText(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
            }
        }

        return cleaned.Trim('`', '"', '\'', ' ', '\t', '\r', '\n');
    }

    private static MeetingTurnDecision? TryParseDecision(string raw)
    {
        var cleaned = CleanDecisionText(raw);
        var jsonStart = cleaned.IndexOf('{');
        var jsonEnd = cleaned.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < jsonStart)
        {
            return null;
        }

        var json = cleaned[jsonStart..(jsonEnd + 1)];
        try
        {
            return JsonSerializer.Deserialize(json, MeetingJsonContext.Default.MeetingTurnDecision);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeStructuredDecision(string raw)
    {
        var cleaned = CleanDecisionText(raw);
        if (cleaned.StartsWith('{') || cleaned.StartsWith('['))
        {
            return true;
        }

        return ContainsAny(cleaned, [
            "\"executionPlan\"",
            "\"currentPhaseId\"",
            "\"nextSpeakerId\"",
            "\"readyForSpeaker\""
        ]);
    }

    private MeetingFlowSnapshot AnalyzeFlow()
    {
        var messages = _messages
            .ListByMeeting(_meetingId)
            .Where(IsUsableForFlow)
            .ToList();
        var latest = messages.LastOrDefault();
        var latestUser = messages.LastOrDefault(m => IsUserMessage(m) && !IsOpeningContextMessage(m));
        var latestInterrupt = messages.LastOrDefault(IsUserInterruptMessage);
        var latestHost = messages.LastOrDefault(IsHostMessage);
        var latestSummary = messages.LastOrDefault(m => string.Equals(m.MessageRole, "summary", StringComparison.OrdinalIgnoreCase));

        if (latestHost is null)
        {
            return new MeetingFlowSnapshot(
                MeetingFlowStage.RequirementConfirmationOrOpening,
                "会议只有主题上下文或历史缺少主持人开场，必须先由主持人确认需求并开场。",
                messages,
                true);
        }

        if (latestInterrupt is not null && latestHost.Sequence < latestInterrupt.Sequence)
        {
            return new MeetingFlowSnapshot(
                MeetingFlowStage.UserInterrupt,
                "老板在会议过程中插入了补充、纠错或范围调整，必须先由主持人判断影响范围并确定恢复点，不能从零重开会议。",
                messages,
                true);
        }

        if (latestUser is not null && latestHost.Sequence < latestUser.Sequence)
        {
            return new MeetingFlowSnapshot(
                MeetingFlowStage.RequirementConfirmationOrOpening,
                "老板在主持人之后补充了需求或回复了澄清问题，必须先由主持人吸收并重新确认流程。",
                messages,
                true);
        }

        if (latestSummary is not null && latestSummary.Sequence >= latestHost.Sequence - 1)
        {
            return new MeetingFlowSnapshot(
                MeetingFlowStage.EndConfirmation,
                "会议已经输出总结，必须由主持人检查插话并征询老板是否结束。",
                messages,
                true);
        }

        if (latest is not null &&
            string.Equals(latest.SpeakerKind, "agent", StringComparison.OrdinalIgnoreCase) &&
            !IsEffectiveParticipantMessage(latest))
        {
            return new MeetingFlowSnapshot(
                MeetingFlowStage.Recovery,
                "上一位参会者没有给出有效业务输入，必须由主持人接管纠偏，避免无效发言循环。",
                messages,
                true);
        }

        return new MeetingFlowSnapshot(
            MeetingFlowStage.Dispatching,
            "主持人已完成当前需求确认或开场，可以按业务依赖调度参会者。",
            messages,
            false);
    }

    private AIAgent ApplyDispatchGuards(
        AIAgent picked,
        MeetingTurnDecision? decision,
        MeetingDispatchPlan dispatchPlan,
        MeetingFlowSnapshot flow,
        AIAgent hostAgent)
    {
        if (ReferenceEquals(picked, hostAgent))
        {
            if (CanHostTakeOver(decision, dispatchPlan, flow))
            {
                return picked;
            }

            var hostFallback = PickCurrentPhaseParticipant(dispatchPlan) ?? _participants.FirstOrDefault();
            if (hostFallback is not null)
            {
                _logger.LogWarning(
                    "会议 {MeetingId} 调度阶段禁止主持人代替参会者发言，切换到真实参会者 {AgentId}：Stage={Stage}, Phase={Phase}",
                    _meetingId,
                    hostFallback.Id,
                    flow.Stage,
                    dispatchPlan.CurrentPhase?.Name ?? "none");
                return hostFallback;
            }

            return picked;
        }

        if (decision is not null && !decision.ReadyForSpeaker)
        {
            return hostAgent;
        }

        if (dispatchPlan.CurrentPhase is null || dispatchPlan.CurrentPhase.AllowedAgentIds.Count == 0)
        {
            return picked;
        }

        if (dispatchPlan.CurrentPhase.AllowedAgentIds.Contains(picked.Id))
        {
            return picked;
        }

        var fallback = PickCurrentPhaseParticipant(dispatchPlan);
        if (fallback is null)
        {
            return picked;
        }

        _logger.LogWarning(
            "会议 {MeetingId} 调度越过业务阶段：Stage={Stage}, Phase={Phase}, Picked={PickedAgentId}, Fallback={FallbackAgentId}, Reason={Reason}",
            _meetingId,
            flow.Stage,
            dispatchPlan.CurrentPhase.Name,
            picked.Id,
            fallback.Id,
            dispatchPlan.CurrentPhase.Reason);
        return fallback;
    }

    private bool CanHostTakeOver(
        MeetingTurnDecision? decision,
        MeetingDispatchPlan dispatchPlan,
        MeetingFlowSnapshot flow)
    {
        if (flow.Stage != MeetingFlowStage.Dispatching)
        {
            return true;
        }

        if (_participants.Count == 0)
        {
            return true;
        }

        if (decision is not null && !decision.ReadyForSpeaker)
        {
            return true;
        }

        if (dispatchPlan.CurrentPhase?.AllowedAgentIds.Count > 0)
        {
            return false;
        }

        return decision is not null && IsHostOnlyNeed(decision);
    }

    private static bool IsHostOnlyNeed(MeetingTurnDecision decision)
    {
        var text = $"{decision.Phase} {decision.Need} {decision.Reason}";
        return ContainsAny(text, [
            "总结",
            "收口",
            "结束",
            "征询",
            "确认",
            "澄清",
            "老板",
            "用户",
            "插话",
            "纠偏"
        ]);
    }

    private AIAgent? PickCurrentPhaseParticipant(MeetingDispatchPlan dispatchPlan)
    {
        return dispatchPlan.CurrentPhase?.AllowedAgentIds
            .Select(id => _participantById.GetValueOrDefault(id))
            .FirstOrDefault(agent => agent is not null);
    }

    private MeetingDispatchPlan BuildDispatchPlan(
        IReadOnlyList<MeetingMessageEntity> messages,
        MeetingTurnDecision? decision)
    {
        var turnDecision = decision;
        var executionPlan = turnDecision?.ExecutionPlan;
        if (executionPlan?.Phases is null || executionPlan.Phases.Count == 0)
        {
            return MeetingDispatchPlan.Empty;
        }

        var phases = executionPlan.Phases
            .Select(ToDispatchPhase)
            .Where(phase => phase.AllowedAgentIds.Count > 0)
            .ToList();
        if (phases.Count == 0)
        {
            return MeetingDispatchPlan.Empty;
        }

        var currentPhaseId = turnDecision?.CurrentPhaseId;
        var currentPhase = !string.IsNullOrWhiteSpace(currentPhaseId)
            ? phases.FirstOrDefault(phase => string.Equals(phase.Id, currentPhaseId, StringComparison.OrdinalIgnoreCase))
            : null;
        currentPhase ??= phases.FirstOrDefault(phase => !HasEffectiveSpeechInPhase(messages, phase));

        return new MeetingDispatchPlan(phases, currentPhase);
    }

    private MeetingDispatchPhase ToDispatchPhase(MeetingExecutionPhase phase)
    {
        var allowedAgentIds = (phase.AllowedAgentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && _participantById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reason = string.IsNullOrWhiteSpace(phase.Goal)
            ? phase.CompletionCriteria
            : phase.Goal;

        return new MeetingDispatchPhase(
            phase.Id ?? string.Empty,
            phase.Name ?? string.Empty,
            reason ?? string.Empty,
            allowedAgentIds);
    }

    private static bool HasEffectiveSpeechInPhase(
        IReadOnlyList<MeetingMessageEntity> messages,
        MeetingDispatchPhase phase)
    {
        return messages.Any(message =>
            string.Equals(message.SpeakerKind, "agent", StringComparison.OrdinalIgnoreCase) &&
            phase.AllowedAgentIds.Contains(message.SpeakerId) &&
            IsEffectiveParticipantMessage(message));
    }

    private static bool IsHostMessage(MeetingMessageEntity message)
    {
        return string.Equals(message.SpeakerKind, "host", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.SpeakerId, HostAgentId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserMessage(MeetingMessageEntity message)
    {
        return string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpeningContextMessage(MeetingMessageEntity message)
    {
        return IsUserMessage(message) &&
            message.ContentMd.TrimStart().StartsWith(OpeningMessagePrefix, StringComparison.Ordinal);
    }

    private static bool IsUserInterruptMessage(MeetingMessageEntity message)
    {
        return IsUserMessage(message) &&
            string.Equals(message.MessageRole, "interrupt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableForFlow(MeetingMessageEntity message)
    {
        if (!message.IsPartial)
        {
            return true;
        }

        return IsHostMessage(message) && !string.IsNullOrWhiteSpace(message.ContentMd);
    }

    private static bool IsEffectiveParticipantMessage(MeetingMessageEntity message)
    {
        if (string.IsNullOrWhiteSpace(message.ContentMd))
        {
            return false;
        }

        var content = message.ContentMd.Trim();
        if (content.Length < 20)
        {
            return false;
        }

        return !ContainsAny(content, [
            "我先查看附件",
            "我先看一下附件",
            "先查一下附件",
            "查一下附件",
            "稍后再",
            "后续再",
            "无法给出",
            "无法判断",
            "没有足够信息"
        ]);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords)
    {
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildHostFlowControlPrompt(MeetingFlowSnapshot flow)
    {
        if (flow.Stage == MeetingFlowStage.UserInterrupt)
        {
            return """
            [系统流程控制：老板插话处理]
            你现在必须先处理老板刚刚插入的补充、纠错或范围调整。
            - 不要重新开场，不要从零开始复述会议规则。
            - 先承接已经完成的有效讨论，再判断老板插话属于：补充、纠错、范围变化、优先级变化、结束请求或无关信息。
            - 明确说明受影响的阶段/结论，以及哪些已经完成的内容仍然有效。
            - 如果插话让上游输入变化，只让受影响的阶段局部回退或复核；不要推翻全部历史。
            - 如果需要老板进一步确认，调用 ask_user；否则只输出简短的插话处理和恢复说明，后续由系统调度器选择真实参会智能体。
            - 不要代替任何参会智能体发表部门观点。
            """;
        }

        if (flow.Stage == MeetingFlowStage.Recovery)
        {
            return """
            [系统流程控制：会议纠偏]
            上一位参会者没有给出有效业务输入。你必须说明当前缺口，并决定是查询附件、向老板澄清，还是让系统调度器恢复到正确阶段。
            不要从零重开会议，不要代替参会者发言。
            """;
        }

        if (flow.Stage == MeetingFlowStage.EndConfirmation)
        {
            return """
            [系统流程控制：总结后确认]
            会议已经输出总结。你必须先检查老板是否有新插话或修改意见，再征询老板是否结束。
            不要重新开始原始议题。
            """;
        }

        return """
        [系统流程控制：需求确认或开场]
        请承接已有会议历史推进。若已有主持人开场或参会者观点，不要从零重新开场。
        """;
    }

    private string BuildSelectorPrompt(
        IReadOnlyList<ChatMessage> history,
        MeetingFlowSnapshot flow)
    {
        var participants = _participants.Count == 0
            ? "（暂无参会者）"
            : string.Join("\n", _participants.Select(p => $"- {p.Name}: {p.Id}"));
        var lastMessages = history.Count == 0
            ? "（暂无历史）"
            : string.Join("\n", history.TakeLast(8).Select(m =>
            {
                var author = string.IsNullOrWhiteSpace(m.AuthorName) ? m.Role.ToString() : m.AuthorName;
                return $"{author}: {m.Text}";
            }));

        return $$"""
        你现在只负责生成或延续会议执行计划，并根据计划选择下一位真实发言者。

        可选发言者：
        - HOST：由主持人发言、澄清、总结或调用工具
        {{participants}}

        固定宏流程：
        - 需求确认：主题、目标、范围、约束、成功标准不清楚时选择 HOST 澄清。
        - 主持人开场：主题清楚且已有主持人开场后进入调度执行。
        - 调度执行：生成适配当前会议主题的 executionPlan，按阶段前置依赖选择真实参会智能体。
        - 阶段纠偏：真实参会者无效发言或需要老板确认时选择 HOST。
        - 插话处理：老板插话已由主持人吸收后，按影响范围局部恢复执行；不能重开会议。
        - 总结与结束：信息成熟后选择 HOST 总结、征询老板并归档。

        调度规则：
        - executionPlan 必须根据本次会议主题动态生成，不能套用固定成本核算模板。
        - 每个 phase 表示一个可验证的信息阶段，allowedAgentIds 只能填写真实参会者 ID，不能填写 HOST 或老板。
        - 阶段顺序必须按业务前置依赖排列：先让能定义上游输入的角色发言，再让依赖这些输入的角色发言。
        - 必须按“执行计划当前阶段 -> 已有信息 -> 信息缺口 -> 前置依赖 -> 最合适补齐缺口的角色”推理。
        - 不要按轮流发言选择；不要因为某位参会者尚未发言就选择它。
        - 下游角色缺少上游输入时，不能选择下游角色硬发言；应选择能补齐上游信息的参会者，或选择 HOST 澄清。
        - 用户是老板，不能被选择为发言者；需要老板补充信息、确认范围或结束会议时选择 HOST。
        - 尽量避免同一位参会者连续发言，除非业务依赖明确需要它继续补齐信息。
        - 当前阶段存在可用真实参会者时，nextSpeakerId 必须是 currentPhaseId 对应阶段 allowedAgentIds 中的一员。
        - 主持人不能代写任何部门观点；如果需要某部门观点，nextSpeakerId 必须选择该真实参会智能体。
        - 如果最近历史包含老板插话/流程变更事件，必须判断 interruptType、affectedPhaseIds、invalidatedPhaseIds 和 resumeAction。
        - 插话只允许局部影响计划：补充信息通常继续当前阶段；纠错上游输入时回退到受影响上游阶段；范围变化时保留旧讨论并更新后续阶段；结束请求选择 HOST。
        - 不要因为老板插话而重新生成完全无关的新会议；executionPlan 应保留仍有效的阶段，只调整受影响阶段和后续阶段。

        只输出一个 JSON 对象，不要解释，不要使用 Markdown：
        {
          "executionPlan": {
            "topic": "本次会议主题",
            "assumptions": ["生成计划采用的必要假设"],
            "phases": [
              {
                "id": "稳定阶段 ID",
                "name": "阶段名称",
                "goal": "阶段目标",
                "allowedAgentIds": ["真实参会者 ID"],
                "prerequisites": ["进入该阶段需要满足的前置条件"],
                "completionCriteria": "阶段完成标准"
              }
            ]
          },
          "currentPhaseId": "当前阶段 ID，若需要 HOST 澄清则可为空",
          "phase": "当前业务阶段",
          "need": "当前最关键的信息缺口",
          "nextSpeakerId": "HOST 或参会者 ID",
          "reason": "为什么此时应由该发言者补齐缺口",
          "prerequisites": ["该发言者有效发言所需的前置信息"],
          "readyForSpeaker": true,
          "interruptType": "none | supplement | correction | scope_change | priority_change | end_request | irrelevant",
          "affectedPhaseIds": ["受老板插话影响的阶段 ID"],
          "invalidatedPhaseIds": ["因插话需要复核或局部回退的阶段 ID"],
          "resumeAction": "continue_current | revisit_phase | update_future_plan | ask_user | summarize"
        }

        如果当前缺口只能由主持人澄清或征询老板，nextSpeakerId 输出 HOST，readyForSpeaker 输出 false。

        程序流程状态：
        - 当前状态：{{flow.Stage}}
        - 状态原因：{{flow.Reason}}

        最近历史：
        {{lastMessages}}
        """;
    }

    private async Task ForceFinalSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hostAgent = EnsureHostAgent();
            var freshHistory = (await UpdateHistoryAsync([], cancellationToken).ConfigureAwait(false)).ToList();
            freshHistory.Add(new ChatMessage(
                ChatRole.System,
                "会议达到最大轮次。请立即输出一份完整会议总结，并调用 output_summary 工具提交。不要再追问，不要再选择参会者。"));

            await InvokeWithLlmRetryAsync(
                    () => hostAgent.RunAsync(freshHistory, cancellationToken: cancellationToken),
                    nameof(ForceFinalSummaryAsync),
                    cancellationToken)
                .ConfigureAwait(false);

            var latestSummary = await _messages.GetLatestSummaryAsync(_meetingId, cancellationToken).ConfigureAwait(false);
            if (latestSummary is not null)
            {
                _sessions.SetFinalSummary(_meetingId, latestSummary.ContentMd);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会议 {MeetingId} 强制总结失败，仍将终止会议", _meetingId);
        }
    }

    private async Task<TResult> InvokeWithLlmRetryAsync<TResult>(
        Func<Task<TResult>> action,
        string operationName,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                _logger.LogInformation("会议 {MeetingId} 的 LLM 调用 {OperationName} 开始", _meetingId, operationName);
                var result = await action().ConfigureAwait(false);
                _logger.LogInformation(
                    "会议 {MeetingId} 的 LLM 调用 {OperationName} 完成：ElapsedMs={ElapsedMs}",
                    _meetingId,
                    operationName,
                    stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                var (category, maxRetries) = FailureClassifier.Classify(ex, ex.Message);
                if (category != FailureCategory.Network || attempt >= maxRetries)
                {
                    _logger.LogError(ex,
                        "会议 {MeetingId} 的 LLM 调用 {OperationName} 失败（{Category}，第 {Attempt}/{MaxRetries} 次），不再重试",
                        _meetingId,
                        operationName,
                        category,
                        attempt,
                        maxRetries);
                    throw;
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex,
                    "会议 {MeetingId} 的 LLM 调用 {OperationName} 网络错误，第 {Attempt}/{MaxRetries} 次，{DelaySeconds}s 后重试",
                    _meetingId,
                    operationName,
                    attempt,
                    maxRetries,
                    delay.TotalSeconds);

                await _publisher.PublishAsync(
                    Events.OnMeetingLlmRetrying,
                    new MeetingLlmRetryingArgs(_meetingId, operationName, attempt, maxRetries, ex.Message));

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private enum MeetingFlowStage
    {
        RequirementConfirmationOrOpening,
        UserInterrupt,
        Dispatching,
        Recovery,
        EndConfirmation
    }

    private sealed record MeetingFlowSnapshot(
        MeetingFlowStage Stage,
        string Reason,
        IReadOnlyList<MeetingMessageEntity> Messages,
        bool MustSelectHost);

    private sealed record MeetingDispatchPlan(
        IReadOnlyList<MeetingDispatchPhase> Phases,
        MeetingDispatchPhase? CurrentPhase)
    {
        public static MeetingDispatchPlan Empty { get; } = new([], null);
    }

    private sealed record MeetingDispatchPhase(
        string Id,
        string Name,
        string Reason,
        IReadOnlySet<string> AllowedAgentIds);
}
