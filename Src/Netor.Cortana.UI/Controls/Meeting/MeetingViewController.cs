using System.Text.Json;
using System.Diagnostics;

using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls.Common;

namespace Netor.Cortana.UI.Controls.Meeting;

/// <summary>
/// 会议 UI 控制器：订阅会议事件，驱动流式气泡、状态提示和历史回放。
/// </summary>
public sealed class MeetingViewController
{
    private const string CompletedStatusText = "本轮会议已归档，可继续讨论、保存记录或开启下一轮";

    private readonly MeetingView _view;
    private readonly ISubscriber _subscriber;
    private readonly MeetingExecutor _executor;
    private readonly MeetingSessionService _sessions;
    private readonly MeetingMessageService _messages;
    private readonly AgentFileService _agentFiles;
    private readonly ICurrentSessionResolver _sessionResolver;
    private readonly Dictionary<string, MeetingBubbleHandle> _bubbles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpeakerSnapshot> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RealtimeProcessCard> _thinkingCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _thinkingCardContainers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolProcessGroupCard> _toolGroupsByMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _toolGroupContainersByMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeProcessCardHandle> _toolHandlesByCallKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stopwatch> _toolStopwatches = new(StringComparer.Ordinal);

    private string? _currentMeetingId;
    private bool _isVisible;

    public MeetingViewController(
        MeetingView view,
        ISubscriber subscriber,
        MeetingExecutor executor,
        MeetingSessionService sessions,
        MeetingMessageService messages,
        AgentFileService agentFiles,
        ICurrentSessionResolver sessionResolver)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _agentFiles = agentFiles ?? throw new ArgumentNullException(nameof(agentFiles));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));

        SubscribeEvents();
    }

    public string? CurrentMeetingId => _currentMeetingId;

    public void SetVisibility(bool isVisible)
    {
        _isVisible = isVisible;
        if (isVisible)
        {
            _view.ForceScrollToBottom();
        }
    }

    public async Task SubmitUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_currentMeetingId))
        {
            return;
        }

        if (string.Equals(text.Trim(), "/end", StringComparison.OrdinalIgnoreCase))
        {
            await _executor.CancelAsync(_currentMeetingId, cancellationToken);
            return;
        }

        var session = _sessions.GetById(_currentMeetingId);
        if (session is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.PendingRequestId))
        {
            await _executor.ResumeAsync(_currentMeetingId, text, cancellationToken);
        }
        else
        {
            await _executor.EnqueueInterruptAsync(_currentMeetingId, text, cancellationToken);
        }

        if (session.Status is 0 or 2 or 3 && !_executor.IsRunning(_currentMeetingId))
        {
            var meetingId = _currentMeetingId;
            if (session.Status is 2 or 3)
            {
                _sessions.SetStatus(meetingId, 0);
            }

            _view.SetReadOnly(false);
            _view.SetStatus("主持人正在继续会议...", "#a0a0a0");
            _view.SetMeetingRunning(meetingId, true);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _executor.ExecuteAsync(meetingId, cancellationToken: cancellationToken);
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => _view.SetMeetingRunning(meetingId, false));
                }
            }, cancellationToken);
        }
    }

    public Task LoadMeetingHistoryAsync(string meetingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        _currentMeetingId = meetingId;
        _view.SetActiveMeeting(meetingId);

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            _bubbles.Clear();
            _speakers.Clear();
            _thinkingCards.Clear();
            _thinkingCardContainers.Clear();
            _toolGroupsByMessageId.Clear();
            _toolGroupContainersByMessageId.Clear();
            _toolHandlesByCallKey.Clear();
            _toolStopwatches.Clear();
            _view.ClearMessages();

            var session = _sessions.GetById(meetingId);
            if (session is not null)
            {
                _view.RenderMembers(ParseParticipantNames(session));
                ApplySessionStatus(session);
            }

            foreach (var message in _messages.ListByMeeting(meetingId))
            {
                RenderHistoryMessage(message);
            }

            _view.ScrollToBottomOnNextLayout();
        }).GetTask();
    }

    private void SubscribeEvents()
    {
        _subscriber.Subscribe<MeetingCreatedArgs>(Events.OnMeetingCreated, (_, args) =>
        {
            PostIfCurrentOrNew(args.MeetingId, () => OnMeetingCreated(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingSpeakerChangedArgs>(Events.OnMeetingSpeakerChanged, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingSpeakerChanged(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingMessageDeltaArgs>(Events.OnMeetingMessageDelta, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingMessageDelta(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingThinkingDeltaArgs>(Events.OnMeetingThinkingDelta, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingThinkingDelta(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingToolCallArgs>(Events.OnMeetingToolCall, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingToolCall(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingToolResultArgs>(Events.OnMeetingToolResult, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingToolResult(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingMessageCompletedArgs>(Events.OnMeetingMessageCompleted, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingMessageCompleted(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingMessageDiscardedArgs>(Events.OnMeetingMessageDiscarded, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingMessageDiscarded(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingAskUserRequestedArgs>(Events.OnMeetingAskUserRequested, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingAskUserRequested(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingUserSpokeArgs>(Events.OnMeetingUserSpoke, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingUserSpoke(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingCompletedArgs>(Events.OnMeetingCompleted, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingCompleted(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingCancelledArgs>(Events.OnMeetingCancelled, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingCancelled(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingPausedArgs>(Events.OnMeetingPaused, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingPaused(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingMessagePartialSavedArgs>(Events.OnMeetingMessagePartialSaved, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => OnMeetingMessagePartialSaved(args));
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<MeetingLlmRetryingArgs>(Events.OnMeetingLlmRetrying, (_, args) =>
        {
            PostIfCurrent(args.MeetingId, () => _view.SetStatus(
                $"网络抖动,正在重试...（第 {args.Attempt}/{args.MaxAttempts} 次）",
                "#F0A030"));
            return Task.FromResult(false);
        });
    }

    private void OnMeetingCreated(MeetingCreatedArgs args)
    {
        _currentMeetingId = args.MeetingId;
        _view.SetActiveMeeting(args.MeetingId);
        _view.ClearMessages();
        _view.SetReadOnly(false);
        _view.RenderMembers(args.Participants.Select(p => p.Name));
        _view.SetStatus("主持人正在组织会议...", "#a0a0a0");
    }

    private void OnMeetingSpeakerChanged(MeetingSpeakerChangedArgs args)
    {
        _speakers[args.SpeakerId] = new SpeakerSnapshot(args.SpeakerId, args.SpeakerName, args.SpeakerKind);
        _view.SetStatus($"{args.SpeakerName} 正在发言...", "#a0a0a0");
    }

    private void OnMeetingMessageDelta(MeetingMessageDeltaArgs args)
    {
        CompleteProcessCards(args.MessageId);
        var speaker = ResolveSpeaker(args.SpeakerId, args.SpeakerKind, args.SpeakerName);
        var handle = EnsureBubble(args.MessageId, speaker.Id, speaker.Name, speaker.Kind, "normal");
        handle.AppendMarkdown(args.DeltaText);
        if (_isVisible)
        {
            _view.AutoScrollToBottom();
        }
    }

    private void OnMeetingThinkingDelta(MeetingThinkingDeltaArgs args)
    {
        if (!_thinkingCards.TryGetValue(args.MessageId, out var card))
        {
            card = CreateProcessCard(args.MessageId, "thinking", "思考", string.Empty);
            _thinkingCards[args.MessageId] = card;
            _thinkingCardContainers[args.MessageId] = _view.AddProcessCard(card);
        }

        card.AppendContent(args.DeltaText);
    }

    private void OnMeetingToolCall(MeetingToolCallArgs args)
    {
        var group = EnsureToolGroup(args.MessageId);
        var handle = group.AddTool(new RealtimeProcessEvent
        {
            ProcessId = string.IsNullOrWhiteSpace(args.CallId) ? Guid.NewGuid().ToString("N") : args.CallId,
            Kind = "tool",
            Title = $"调用工具 {args.ToolName}",
            Status = "running",
            Content = args.ArgsJson,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var callKey = BuildToolCardKey(args.MessageId, args.CallId);
        _toolHandlesByCallKey[callKey] = handle;
        _toolStopwatches[callKey] = Stopwatch.StartNew();
    }

    private void OnMeetingToolResult(MeetingToolResultArgs args)
    {
        var callKey = BuildToolCardKey(args.MessageId, args.CallId);
        if (!_toolHandlesByCallKey.TryGetValue(callKey, out var handle))
        {
            var toolGroup = EnsureToolGroup(args.MessageId);
            handle = toolGroup.AddTool(new RealtimeProcessEvent
            {
                ProcessId = string.IsNullOrWhiteSpace(args.CallId) ? Guid.NewGuid().ToString("N") : args.CallId,
                Kind = "tool",
                Title = "调用工具",
                Status = "running",
                Content = string.Empty,
                Timestamp = DateTimeOffset.UtcNow,
            });
            _toolHandlesByCallKey[callKey] = handle;
        }

        var durationMs = 0L;
        if (_toolStopwatches.Remove(callKey, out var stopwatch))
        {
            stopwatch.Stop();
            durationMs = stopwatch.ElapsedMilliseconds;
        }

        var failed = !string.IsNullOrWhiteSpace(args.ExceptionMessage);
        var content = failed ? args.ExceptionMessage! : args.ResultText ?? "工具已完成。";
        handle.AppendContent(content);
        handle.Complete(failed ? "failed" : "success", null, durationMs);

        if (_toolGroupsByMessageId.TryGetValue(args.MessageId, out var existingGroup))
        {
            existingGroup.CompleteIfIdle();
        }
    }

    private void OnMeetingMessageCompleted(MeetingMessageCompletedArgs args)
    {
        var speaker = ResolveSpeaker(args.SpeakerId, args.SpeakerKind, args.SpeakerName);
        var handle = EnsureBubble(args.MessageId, speaker.Id, speaker.Name, speaker.Kind, args.MessageRole);
        handle.MarkdownBuffer.Clear();
        handle.AppendMarkdown(args.ContentMd);
        handle.FlushMarkdown();
        CompleteProcessCards(args.MessageId);

        if (args.AwaitingUserReply)
        {
            _view.SetStatus("主持人在等您回复", "#A06CFF");
        }

    }

    private void OnMeetingMessageDiscarded(MeetingMessageDiscardedArgs args)
    {
        if (_bubbles.Remove(args.MessageId, out var handle))
        {
            _view.Messages.Items.Remove(handle.Root);
        }

        RemoveProcessCards(args.MessageId);
    }

    private void OnMeetingAskUserRequested(MeetingAskUserRequestedArgs args)
    {
        _view.SetStatus("主持人在等您回复", "#A06CFF");
    }

    private void OnMeetingUserSpoke(MeetingUserSpokeArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.MessageId))
        {
            return;
        }

        _view.SetStatus(args.Kind == "reply" ? "已收到您的回复" : "已记录您的插话", "#a0a0a0");
    }

    private void OnMeetingCompleted(MeetingCompletedArgs args)
    {
        _view.SetStatus(CompletedStatusText, "#A06CFF");
        _view.SetReadOnly(false);
        _view.SetMeetingIdle(args.MeetingId);
    }

    private void OnMeetingCancelled(MeetingCancelledArgs args)
    {
        _view.SetStatus("会议已散会（只读模式）", "#7a7a7a");
        _view.SetReadOnly(true);
        _view.AddSystemMessage($"会议已取消：{args.Reason}");
    }

    private void OnMeetingPaused(MeetingPausedArgs args)
    {
        _view.SetStatus("讨论已暂停，可继续输入总结或讨论指令", "#A06CFF");
        _view.SetReadOnly(false);
        _view.SetMeetingRunning(args.MeetingId, false);
    }

    private void OnMeetingMessagePartialSaved(MeetingMessagePartialSavedArgs args)
    {
        if (_bubbles.TryGetValue(args.MessageId, out var handle))
        {
            handle.AppendMarkdown(args.PartialContent);
            handle.FlushMarkdown();
            handle.MarkPartial(args.ErrorMessage);
            CompleteProcessCards(args.MessageId);
        }

        _view.SetStatus("部分消息已保存", "#F0A030");
    }

    private void RenderHistoryMessage(MeetingMessageEntity message)
    {
        var handle = MeetingBubbleBuilder.Build(new MeetingBubbleArgs(
            message.Id,
            message.SpeakerKind,
            message.SpeakerId,
            message.SpeakerName,
            message.ContentMd,
            message.MessageRole,
            DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt),
            message.IsPartial,
            message.ErrorMessage,
            ResolveAvatarPath(message.SpeakerKind, message.SpeakerId)));

        if (!string.IsNullOrWhiteSpace(message.ThinkingMd))
        {
            var card = CreateProcessCard($"{message.Id}:thinking", "thinking", "思考", message.ThinkingMd);
            card.CompleteCollapsed("success", null, 0);
            _thinkingCards[message.Id] = card;
            _thinkingCardContainers[message.Id] = _view.AddProcessCard(card);
        }

        var historyTools = ParseToolCalls(message.ToolCallsJson);
        if (historyTools.Count > 0)
        {
            var group = new ToolProcessGroupCard($"history:{message.Id}");
            foreach (var tool in historyTools)
            {
                var processId = string.IsNullOrWhiteSpace(tool.CallId) ? Guid.NewGuid().ToString("N") : tool.CallId;
                var child = group.AddTool(new RealtimeProcessEvent
                {
                    ProcessId = processId,
                    Kind = "tool",
                    Title = $"调用工具 {tool.Name}",
                    Status = "running",
                    Content = BuildToolHistoryContent(tool),
                    DurationMs = tool.DurationMs,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt),
                });
                child.Complete(string.IsNullOrWhiteSpace(tool.ExceptionMessage) ? "success" : "failed", null, tool.DurationMs);
            }

            group.CompleteIfIdle();
            _toolGroupsByMessageId[message.Id] = group;
            _toolGroupContainersByMessageId[message.Id] = _view.AddToolProcessGroupCard(group);
        }

        _bubbles[message.Id] = handle;
        _view.AddBubble(handle);
    }

    public void StartNewMeeting()
    {
        _currentMeetingId = null;
        _bubbles.Clear();
        _speakers.Clear();
        _thinkingCards.Clear();
        _thinkingCardContainers.Clear();
        _toolGroupsByMessageId.Clear();
        _toolGroupContainersByMessageId.Clear();
        _toolHandlesByCallKey.Clear();
        _toolStopwatches.Clear();
        _view.ClearMessages();
        _view.SetActiveMeeting(null);
        _view.SetReadOnly(false);
        _view.SetStatus("选择参会者并输入会议主题", "#a0a0a0");
    }

    private MeetingBubbleHandle EnsureBubble(
        string messageId,
        string speakerId,
        string speakerName,
        string speakerKind,
        string messageRole)
    {
        if (_bubbles.TryGetValue(messageId, out var handle))
        {
            return handle;
        }

        var bubble = MeetingBubbleBuilder.Build(new MeetingBubbleArgs(
            messageId,
            speakerKind,
            speakerId,
            speakerName,
            string.Empty,
            messageRole,
            DateTimeOffset.Now,
            AvatarPath: ResolveAvatarPath(speakerKind, speakerId)));
        _bubbles[messageId] = bubble;
        _view.AddBubble(bubble);
        return bubble;
    }

    private SpeakerSnapshot ResolveSpeaker(string speakerId, string? speakerKind = null, string? speakerName = null)
    {
        if (_speakers.TryGetValue(speakerId, out var speaker))
        {
            return !string.IsNullOrWhiteSpace(speakerKind) && !string.Equals(speaker.Kind, speakerKind, StringComparison.OrdinalIgnoreCase)
                ? speaker with { Kind = speakerKind }
                : speaker;
        }

        var kind = string.IsNullOrWhiteSpace(speakerKind) ? "agent" : speakerKind;
        if (!string.IsNullOrWhiteSpace(speakerName))
        {
            return new SpeakerSnapshot(speakerId, speakerName.Trim(), kind);
        }

        return string.Equals(kind, "host", StringComparison.OrdinalIgnoreCase)
            ? new SpeakerSnapshot("system-meeting-host", "主持人", "host")
            : new SpeakerSnapshot(speakerId, string.IsNullOrWhiteSpace(speakerId) ? "未知参会者" : speakerId, kind);
    }

    private string? ResolveAvatarPath(string speakerKind, string speakerId)
    {
        if (!string.Equals(speakerKind, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var record = _agentFiles.GetByName(speakerId);
        if (record is null || string.IsNullOrWhiteSpace(record.Manifest.Avatar) || Path.IsPathRooted(record.Manifest.Avatar))
        {
            return null;
        }

        var agentDirectory = Path.GetFullPath(record.DirectoryPath);
        var avatarPath = Path.GetFullPath(Path.Combine(agentDirectory, record.Manifest.Avatar));
        var rootPrefix = agentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!avatarPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(avatarPath))
        {
            return null;
        }

        return avatarPath;
    }

    private RealtimeProcessCard CreateProcessCard(string processId, string kind, string title, string content)
    {
        var card = new RealtimeProcessCard(new RealtimeProcessEvent
        {
            ProcessId = string.IsNullOrWhiteSpace(processId) ? Guid.NewGuid().ToString("N") : processId,
            Kind = kind,
            Title = title,
            Status = "running",
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
        });
        ApplyStandaloneCardMargin(card);
        return card;
    }

    private static void ApplyStandaloneCardMargin(RealtimeProcessCard card)
    {
        card.Margin = new Thickness(0);
        var rootBorder = card.FindControl<Avalonia.Controls.Border>("RootBorder");
        if (rootBorder is not null)
        {
            rootBorder.Margin = new Thickness(50, 0);
        }
    }

    private void CompleteProcessCards(string messageId)
    {
        if (_thinkingCards.TryGetValue(messageId, out var thinkingCard))
        {
            thinkingCard.CompleteCollapsed("success", null, 0);
        }

        if (_toolGroupsByMessageId.TryGetValue(messageId, out var toolGroup))
        {
            toolGroup.CompleteIfIdle();
        }
    }

    private ToolProcessGroupCard EnsureToolGroup(string messageId)
    {
        if (_toolGroupsByMessageId.TryGetValue(messageId, out var group))
        {
            return group;
        }

        group = new ToolProcessGroupCard(messageId);
        _toolGroupsByMessageId[messageId] = group;
        _toolGroupContainersByMessageId[messageId] = _view.AddToolProcessGroupCard(group);
        return group;
    }

    private void RemoveProcessCards(string messageId)
    {
        if (_thinkingCards.Remove(messageId, out _)
            && _thinkingCardContainers.Remove(messageId, out var thinkingContainer))
        {
            _view.Messages.Items.Remove(thinkingContainer);
        }

        if (_toolGroupsByMessageId.Remove(messageId, out _)
            && _toolGroupContainersByMessageId.Remove(messageId, out var toolContainer))
        {
            _view.Messages.Items.Remove(toolContainer);
        }

        foreach (var callKey in _toolHandlesByCallKey.Keys
            .Where(key => key.StartsWith($"{messageId}:", StringComparison.Ordinal))
            .ToList())
        {
            _toolHandlesByCallKey.Remove(callKey);
            if (_toolStopwatches.Remove(callKey, out var stopwatch))
            {
                stopwatch.Stop();
            }
        }
    }

    private static string BuildToolCardKey(string messageId, string callId)
        => $"{messageId}:{callId}";

    private void ApplySessionStatus(MeetingSessionEntity session)
    {
        switch (session.Status)
        {
            case 0:
                _view.SetReadOnly(false);
                _view.SetStatus("会议进行中", "#a0a0a0");
                break;
            case 1:
                _view.SetReadOnly(false);
                _view.SetStatus("主持人在等您回复", "#A06CFF");
                break;
            case 2:
                _view.SetReadOnly(false);
                _view.SetStatus(CompletedStatusText, "#A06CFF");
                _view.SetMeetingIdle(session.Id);
                break;
            case 3:
                _view.SetReadOnly(false);
                _view.SetStatus("会议已散会，可输入指令基于历史继续", "#A06CFF");
                break;
            default:
                _view.SetReadOnly(true);
                _view.SetStatus("会议状态未知", "#7a7a7a");
                break;
        }
    }

    private void PostIfCurrentOrNew(string meetingId, Action action)
    {
        var current = _currentMeetingId;
        if (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(current, meetingId, StringComparison.Ordinal))
        {
            var session = _sessions.GetById(current);
            if (session is null || session.Status is 0 or 1)
            {
                return;
            }
        }

        Dispatcher.UIThread.Post(action);
    }

    private void PostIfCurrent(string meetingId, Action action)
    {
        if (!string.Equals(_currentMeetingId, meetingId, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private static IEnumerable<string> ParseParticipantNames(MeetingSessionEntity session)
    {
        try
        {
            var participants = JsonSerializer.Deserialize(
                session.ParticipantsJson,
                MeetingJsonContext.Default.ListMeetingParticipantDto);
            return participants?.Select(p => p.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<MeetingToolCallRecord> ParseToolCalls(string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(
                toolCallsJson,
                MeetingJsonContext.Default.ListMeetingToolCallRecord) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildToolHistoryContent(MeetingToolCallRecord tool)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tool.ArgsJson))
        {
            parts.Add(tool.ArgsJson);
        }

        if (!string.IsNullOrWhiteSpace(tool.ResultText))
        {
            parts.Add(tool.ResultText);
        }

        if (!string.IsNullOrWhiteSpace(tool.ExceptionMessage))
        {
            parts.Add(tool.ExceptionMessage);
        }

        return parts.Count == 0 ? "工具调用已记录。" : string.Join(Environment.NewLine, parts);
    }

    private sealed record SpeakerSnapshot(string Id, string Name, string Kind);
}
