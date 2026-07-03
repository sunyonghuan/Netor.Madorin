using System.Text;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议模式流式事件处理器，将 AF Workflow 事件转换为会议事件并持久化消息。
/// </summary>
public sealed class MeetingStreamProcessor
{
    private const string HostAgentId = "system-meeting-host";

    private static readonly HashSet<string> SuppressedToolEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user",
        "output_summary",
        "confirm_meeting_end",
        "check_pending_user_input",
        "list_meeting_attachments"
    };

    private readonly string _meetingId;
    private readonly IReadOnlyDictionary<string, AgentSpeaker> _speakersById;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingMessageService _messages;
    private readonly MeetingCompactionService _compaction;
    private readonly IPublisher _publisher;
    private readonly ILogger<MeetingStreamProcessor> _logger;

    private readonly Dictionary<string, MessageAccumulator> _accumulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AIAgent> _agentsById = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentAgentId;
    private AIAgent? _fallbackAgent;

    public MeetingStreamProcessor(
        string meetingId,
        IReadOnlyList<AIAgent> participants,
        MeetingSessionService sessions,
        MeetingMessageService messages,
        MeetingCompactionService compaction,
        IPublisher publisher,
        ILogger<MeetingStreamProcessor> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(participants);

        _meetingId = meetingId;
        _speakersById = participants
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var agent = g.First();
                    var id = agent.Id ?? g.Key;
                    var name = string.IsNullOrWhiteSpace(agent.Name) ? id : agent.Name;
                    var kind = string.Equals(id, HostAgentId, StringComparison.OrdinalIgnoreCase)
                        ? "host"
                        : "agent";
                    return new AgentSpeaker(id, name, kind);
                },
                StringComparer.OrdinalIgnoreCase);
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var participant in participants.Where(p => !string.IsNullOrWhiteSpace(p.Id)))
        {
            _agentsById[participant.Id!] = participant;
            _fallbackAgent ??= participant;
        }
    }

    /// <summary>处理一条 AF Workflow 事件。</summary>
    public async Task ProcessEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        cancellationToken.ThrowIfCancellationRequested();

        switch (workflowEvent)
        {
            case ExecutorInvokedEvent invoked:
                await OnExecutorInvokedAsync(invoked, cancellationToken).ConfigureAwait(false);
                break;
            case AgentResponseUpdateEvent update:
                await OnAgentResponseUpdateAsync(update, cancellationToken).ConfigureAwait(false);
                break;
            case AgentResponseEvent response:
                await OnAgentResponseAsync(response, cancellationToken).ConfigureAwait(false);
                break;
            case ExecutorCompletedEvent completed:
                await OnExecutorCompletedAsync(completed, cancellationToken).ConfigureAwait(false);
                break;
            case WorkflowOutputEvent output:
                await OnWorkflowOutputAsync(output, cancellationToken).ConfigureAwait(false);
                break;
            case WorkflowErrorEvent error:
                throw error.Exception ?? new InvalidOperationException("会议工作流执行失败，但未提供异常详情。");
        }
    }

    /// <summary>保存仍未定稿的部分消息，供异常路径调用。</summary>
    public async Task SavePartialsAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var accumulator in _accumulators.Values.Where(a => !a.Completed))
        {
            var text = accumulator.Text.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var message = BuildMessage(accumulator, text);
            message.IsPartial = true;
            message.ErrorMessage = exception.Message;
            _messages.UpsertPartialDraft(message);

            accumulator.Completed = true;
            await _publisher.PublishAsync(
                Events.OnMeetingMessagePartialSaved,
                new MeetingMessagePartialSavedArgs(_meetingId, message.Id, message.SpeakerId, text, exception.Message));
        }
    }

    private async Task OnExecutorInvokedAsync(ExecutorInvokedEvent invoked, CancellationToken cancellationToken)
    {
        var speaker = ResolveSpeaker(invoked.ExecutorId, null, null);
        _currentAgentId = speaker.Id;
        _logger.LogInformation(
            "会议 {MeetingId} 发言者切换：ExecutorId={ExecutorId}, Speaker={SpeakerName}({SpeakerId}/{SpeakerKind})",
            _meetingId,
            invoked.ExecutorId,
            speaker.Name,
            speaker.Id,
            speaker.Kind);

        await _publisher.PublishAsync(
            Events.OnMeetingSpeakerChanged,
            new MeetingSpeakerChangedArgs(_meetingId, speaker.Id, speaker.Name, speaker.Kind));
    }

    private async Task OnAgentResponseUpdateAsync(AgentResponseUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        var update = updateEvent.Update;
        var speaker = ResolveSpeaker(update.AgentId, update.AuthorName, updateEvent.ExecutorId);
        _currentAgentId = speaker.Id;
        var accumulator = GetAccumulator(update.ResponseId, speaker);
        _logger.LogInformation(
            "会议 {MeetingId} 收到发言增量：Speaker={SpeakerName}({SpeakerId}), ResponseId={ResponseId}, TextLength={TextLength}, Contents={ContentCount}",
            _meetingId,
            speaker.Name,
            speaker.Id,
            update.ResponseId,
            update.Text?.Length ?? 0,
            update.Contents.Count);

        await ProcessContentsAsync(accumulator, speaker, update.Contents, appendTextOnlyWhenEmpty: false, cancellationToken)
            .ConfigureAwait(false);

        if (accumulator.LastContentBatchHadText || string.IsNullOrEmpty(update.Text))
        {
            return;
        }

        await OnTextDeltaAsync(accumulator, speaker, update.Text).ConfigureAwait(false);
    }

    private async Task OnAgentResponseAsync(AgentResponseEvent responseEvent, CancellationToken cancellationToken)
    {
        var response = responseEvent.Response;
        var speaker = ResolveSpeaker(response.AgentId, null, responseEvent.ExecutorId);
        _currentAgentId = speaker.Id;
        var accumulator = GetAccumulator(response.ResponseId, speaker);
        _logger.LogInformation(
            "会议 {MeetingId} 收到发言最终响应：Speaker={SpeakerName}({SpeakerId}), ResponseId={ResponseId}, TextLength={TextLength}, Messages={MessageCount}",
            _meetingId,
            speaker.Name,
            speaker.Id,
            response.ResponseId,
            response.Text?.Length ?? 0,
            response.Messages.Count);

        foreach (var responseMessage in response.Messages)
        {
            var messageSpeaker = ResolveSpeaker(response.AgentId, responseMessage.AuthorName, responseEvent.ExecutorId);
            await ProcessContentsAsync(accumulator, messageSpeaker, responseMessage.Contents, appendTextOnlyWhenEmpty: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (accumulator.Text.Length == 0 && !string.IsNullOrWhiteSpace(response.Text))
        {
            accumulator.Text.Append(response.Text);
        }

        if (accumulator.Text.Length == 0 &&
            response.Messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text)) is { } message)
        {
            accumulator.Text.Append(message.Text);
        }

        await CompleteAccumulatorAsync(accumulator, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnExecutorCompletedAsync(ExecutorCompletedEvent completedEvent, CancellationToken cancellationToken)
    {
        var speaker = ResolveSpeaker(completedEvent.ExecutorId, null, null);
        foreach (var accumulator in _accumulators.Values
            .Where(a => !a.Completed && string.Equals(a.Speaker.Id, speaker.Id, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            await CompleteAccumulatorAsync(accumulator, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessContentsAsync(
        MessageAccumulator accumulator,
        AgentSpeaker speaker,
        IList<AIContent> contents,
        bool appendTextOnlyWhenEmpty,
        CancellationToken cancellationToken)
    {
        var shouldAppendText = !appendTextOnlyWhenEmpty || accumulator.Text.Length == 0;
        var textContentSeen = false;
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    accumulator.Thinking.Append(reasoning.Text);
                    await _publisher.PublishAsync(
                        Events.OnMeetingThinkingDelta,
                        new MeetingThinkingDeltaArgs(_meetingId, accumulator.MessageId, speaker.Id, reasoning.Text));
                    break;

                case FunctionCallContent functionCall:
                    await OnToolCallAsync(accumulator, functionCall, cancellationToken).ConfigureAwait(false);
                    break;

                case FunctionResultContent functionResult:
                    await OnToolResultAsync(accumulator, functionResult, cancellationToken).ConfigureAwait(false);
                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    textContentSeen = true;
                    if (shouldAppendText)
                    {
                        await OnTextDeltaAsync(accumulator, speaker, text.Text).ConfigureAwait(false);
                    }

                    break;
            }
        }

        accumulator.LastContentBatchHadText = textContentSeen;
    }

    private async Task OnWorkflowOutputAsync(WorkflowOutputEvent output, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "会议 {MeetingId} 收到 WorkflowOutput：DataType={DataType}",
            _meetingId,
            output.Data?.GetType().Name ?? "(null)");

        foreach (var accumulator in _accumulators.Values.Where(a => !a.Completed).ToList())
        {
            await CompleteAccumulatorAsync(accumulator, cancellationToken).ConfigureAwait(false);
        }

        var session = _sessions.GetById(_meetingId);
        var finalSummary = session?.FinalSummaryMd;
        if (string.IsNullOrWhiteSpace(finalSummary))
        {
            finalSummary = output.Data?.ToString() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(finalSummary))
        {
            await _publisher.PublishAsync(
                Events.OnMeetingCompleted,
                CreateMeetingCompletedArgs(finalSummary, session));
        }
    }

    private async Task OnTextDeltaAsync(
        MessageAccumulator accumulator,
        AgentSpeaker speaker,
        string text)
    {
        accumulator.Text.Append(text);
        PersistPartialDraft(accumulator);
        await _publisher.PublishAsync(
            Events.OnMeetingMessageDelta,
            new MeetingMessageDeltaArgs(_meetingId, accumulator.MessageId, speaker.Id, speaker.Kind, text, speaker.Name));
    }

    private async Task OnToolCallAsync(
        MessageAccumulator accumulator,
        FunctionCallContent functionCall,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(functionCall.CallId) &&
            accumulator.Tools.Any(t => string.Equals(t.CallId, functionCall.CallId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var argsJson = FormatJsonValue(functionCall.Arguments);
        accumulator.Tools.Add(new MeetingToolCallRecord(
            functionCall.CallId,
            functionCall.Name,
            argsJson,
            null,
            functionCall.Exception?.Message,
            0));

        if (string.Equals(functionCall.Name, "ask_user", StringComparison.OrdinalIgnoreCase) &&
            accumulator.Text.Length == 0 &&
            functionCall.Arguments is not null &&
            TryGetStringArgument(functionCall.Arguments, "question", out var question))
        {
            accumulator.Text.Append(question);
            PersistPartialDraft(accumulator);
            await _publisher.PublishAsync(
                Events.OnMeetingMessageDelta,
                new MeetingMessageDeltaArgs(
                    _meetingId,
                    accumulator.MessageId,
                    accumulator.Speaker.Id,
                    accumulator.Speaker.Kind,
                    question,
                    accumulator.Speaker.Name));
        }

        if (SuppressedToolEvents.Contains(functionCall.Name))
        {
            return;
        }

        await _publisher.PublishAsync(
            Events.OnMeetingToolCall,
            new MeetingToolCallArgs(_meetingId, accumulator.MessageId, functionCall.CallId, functionCall.Name, argsJson));
    }

    private async Task OnToolResultAsync(
        MessageAccumulator accumulator,
        FunctionResultContent functionResult,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(functionResult.CallId) &&
            accumulator.CompletedToolResultCallIds.Contains(functionResult.CallId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(functionResult.CallId))
        {
            accumulator.CompletedToolResultCallIds.Add(functionResult.CallId);
        }

        var resultText = functionResult.Result?.ToString();
        var exceptionMessage = functionResult.Exception?.Message;
        var index = accumulator.Tools.FindIndex(t => string.Equals(t.CallId, functionResult.CallId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var previous = accumulator.Tools[index];
            accumulator.Tools[index] = previous with
            {
                ResultText = resultText,
                ExceptionMessage = exceptionMessage
            };
            PersistPartialDraft(accumulator);
        }

        if (ShouldSuppressToolResult(functionResult.CallId, accumulator))
        {
            return;
        }

        await _publisher.PublishAsync(
            Events.OnMeetingToolResult,
            new MeetingToolResultArgs(_meetingId, accumulator.MessageId, functionResult.CallId, resultText, exceptionMessage));
    }

    private async Task CompleteAccumulatorAsync(
        MessageAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (accumulator.Completed)
        {
            return;
        }

        var text = accumulator.Text.ToString();
        if (ShouldDiscardMessage(accumulator, text))
        {
            accumulator.Completed = true;
            _messages.DeleteById(accumulator.MessageId);
            await _publisher.PublishAsync(
                Events.OnMeetingMessageDiscarded,
                new MeetingMessageDiscardedArgs(_meetingId, accumulator.MessageId));
            return;
        }

        var message = BuildMessage(accumulator, text);
        _messages.CompleteDraft(message);
        accumulator.Completed = true;
        TriggerCompaction(accumulator.Speaker.Id);

        await _publisher.PublishAsync(
            Events.OnMeetingMessageCompleted,
            CreateMeetingMessageCompletedArgs(message));
    }

    private void TriggerCompaction(string speakerId)
    {
        var agent = _agentsById.TryGetValue(speakerId, out var speakerAgent)
            ? speakerAgent
            : _fallbackAgent;
        if (agent is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _compaction.TryCompactAsync(_meetingId, agent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "会议 {MeetingId} 触发压缩失败", _meetingId);
            }
        });
    }

    private MeetingMessageCompletedArgs CreateMeetingMessageCompletedArgs(MeetingMessageEntity message)
    {
        var session = _sessions.GetById(_meetingId);
        return new MeetingMessageCompletedArgs(
            MeetingId: _meetingId,
            MessageId: message.Id,
            SpeakerId: message.SpeakerId,
            SpeakerKind: message.SpeakerKind,
            ContentMd: message.ContentMd,
            MessageRole: message.MessageRole,
            AwaitingUserReply: message.AwaitingUserReply,
            SessionId: session?.SessionId ?? string.Empty,
            WorkspaceId: session?.WorkspaceId ?? string.Empty,
            Topic: session?.Topic ?? string.Empty,
            HostAgentId: session?.HostAgentId ?? string.Empty,
            Model: session?.Model ?? string.Empty,
            CreatedAt: message.CreatedAt,
            SpeakerName: message.SpeakerName);
    }

    private MeetingCompletedArgs CreateMeetingCompletedArgs(string finalSummary, MeetingSessionEntity? session)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return new MeetingCompletedArgs(
            MeetingId: _meetingId,
            FinalSummary: finalSummary,
            SessionId: session?.SessionId ?? string.Empty,
            WorkspaceId: session?.WorkspaceId ?? string.Empty,
            Topic: session?.Topic ?? string.Empty,
            HostAgentId: session?.HostAgentId ?? string.Empty,
            Model: session?.Model ?? string.Empty,
            CreatedAt: session?.CreatedAt ?? now,
            EndedAt: session?.EndedAt ?? now);
    }

    private void PersistPartialDraft(MessageAccumulator accumulator)
    {
        var text = accumulator.Text.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var message = BuildMessage(accumulator, text);
        message.IsPartial = true;
        message.ErrorMessage = "会议仍在进行，消息尚未最终确认。";
        _messages.UpsertPartialDraft(message);
    }

    private MeetingMessageEntity BuildMessage(MessageAccumulator accumulator, string text)
    {
        return new MeetingMessageEntity
        {
            Id = accumulator.MessageId,
            MeetingId = _meetingId,
            SpeakerKind = accumulator.Speaker.Kind,
            SpeakerId = accumulator.Speaker.Id,
            SpeakerName = accumulator.Speaker.Name,
            ContentMd = text,
            ThinkingMd = accumulator.Thinking.Length > 0 ? accumulator.Thinking.ToString() : null,
            ToolCallsJson = accumulator.Tools.Count > 0
                ? JsonSerializer.Serialize(accumulator.Tools, MeetingJsonContext.Default.ListMeetingToolCallRecord)
                : null,
            MessageRole = ResolveMessageRole(accumulator),
            AwaitingUserReply = IsAwaitingUserReply(),
        };
    }

    private MessageAccumulator GetAccumulator(string? responseId, AgentSpeaker speaker)
    {
        var key = !string.IsNullOrWhiteSpace(responseId)
            ? responseId
            : speaker.Id;

        if (_accumulators.TryGetValue(key, out var accumulator))
        {
            return accumulator;
        }

        if (string.IsNullOrWhiteSpace(responseId) &&
            _accumulators.Values.LastOrDefault(a =>
                !a.Completed &&
                string.Equals(a.Speaker.Id, speaker.Id, StringComparison.OrdinalIgnoreCase)) is { } active)
        {
            return active;
        }

        accumulator = new MessageAccumulator(Guid.NewGuid().ToString("N"), speaker);
        _accumulators[key] = accumulator;
        return accumulator;
    }

    private AgentSpeaker ResolveSpeaker(string? agentId, string? authorName, string? executorId)
    {
        var candidates = new[] { agentId, executorId, authorName, _currentAgentId };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (_speakersById.TryGetValue(candidate, out var exact))
            {
                return exact;
            }

            var fuzzy = _speakersById.Values.FirstOrDefault(s =>
                candidate.Contains(s.Id, StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
            if (fuzzy is not null)
            {
                return fuzzy;
            }
        }

        var name = string.IsNullOrWhiteSpace(authorName)
            ? "主持人"
            : authorName.Trim();
        return new AgentSpeaker("system-meeting-host", name, "host");
    }

    private string ResolveMessageRole(MessageAccumulator accumulator)
    {
        if (accumulator.Tools.Any(t => string.Equals(t.Name, "ask_user", StringComparison.OrdinalIgnoreCase)))
        {
            return "inquiry";
        }

        return "normal";
    }

    private static bool ShouldSkipToolOnlyMessage(MessageAccumulator accumulator)
    {
        if (accumulator.Tools.Count == 0)
        {
            return true;
        }

        return accumulator.Tools.All(t =>
            SuppressedToolEvents.Contains(t.Name) &&
            !string.Equals(t.Name, "ask_user", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldDiscardMessage(MessageAccumulator accumulator, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return accumulator.Tools.Count == 0 || ShouldSkipToolOnlyMessage(accumulator);
    }

    private bool IsAwaitingUserReply()
    {
        return !string.IsNullOrWhiteSpace(_sessions.GetById(_meetingId)?.PendingRequestId);
    }

    private static bool ShouldSuppressToolResult(string callId, MessageAccumulator accumulator)
    {
        var tool = accumulator.Tools.FirstOrDefault(t => string.Equals(t.CallId, callId, StringComparison.OrdinalIgnoreCase));
        return tool is not null && SuppressedToolEvents.Contains(tool.Name);
    }

    private static bool TryGetStringArgument(
        IDictionary<string, object?> arguments,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
        {
            return false;
        }

        value = raw switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => raw.ToString() ?? string.Empty,
        };

        value = value.Trim();
        return value.Length > 0;
    }

    private static string FormatJsonValue(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteJsonValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary)
                {
                    writer.WritePropertyName(key);
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, item) in pairs)
                {
                    writer.WritePropertyName(key);
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<object?> items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private sealed record AgentSpeaker(string Id, string Name, string Kind);

    private sealed class MessageAccumulator(string messageId, AgentSpeaker speaker)
    {
        public string MessageId { get; } = messageId;

        public AgentSpeaker Speaker { get; } = speaker;

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public List<MeetingToolCallRecord> Tools { get; } = [];

        public HashSet<string> CompletedToolResultCallIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Completed { get; set; }

        public bool LastContentBatchHadText { get; set; }
    }
}
