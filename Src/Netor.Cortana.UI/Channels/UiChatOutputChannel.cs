using System.Text;
using System.Collections.Concurrent;

using Avalonia.Threading;

using Netor.Cortana.UI.Controls.Common;
using Netor.Cortana.UI.Controls.ExpertMode;
using Netor.Cortana.UI.Views;

namespace Netor.Cortana.UI;

/// <summary>
/// Avalonia UI 输出通道。将 AI 流式回复实时渲染到 MainWindow 的消息列表中。
/// 每次对话开始时创建一个 AI 消息气泡，后续 token 累积更新该气泡的 Markdown 内容。
/// </summary>
internal sealed class UiChatOutputChannel(
    IServiceProvider serviceProvider,
    ILogger<UiChatOutputChannel> logger) : IAiOutputChannel, IRealtimeProcessOutput
{
    private static readonly TimeSpan CancelledTurnRetention = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, TurnRenderState> _statesByTurnId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cancelledTurnIds = new(StringComparer.Ordinal);
    private string? _latestTurnId;

    /// <inheritdoc />
    public string Name => "UI";

    /// <inheritdoc />
    public bool IsActive => true;

    public Task OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(evt.ProcessId))
        {
            return Task.CompletedTask;
        }

        Dispatcher.UIThread.Post(() => HandleProcessEvent(evt));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnTokenAsync(string turnId, string token, string sessionId, CancellationToken cancellationToken = default)
    {
        if (ShouldIgnoreTurn(turnId, cancellationToken) || string.IsNullOrEmpty(token))
        {
            return Task.CompletedTask;
        }

        _latestTurnId = turnId;
        var state = _statesByTurnId.GetOrAdd(turnId, static _ => new TurnRenderState());
        lock (state.SyncRoot)
        {
            state.Buffer.Append(token);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ShouldIgnoreTurn(turnId, cancellationToken))
            {
                return;
            }

            var latestState = _statesByTurnId.GetOrAdd(turnId, static _ => new TurnRenderState());
            string currentText;
            lock (latestState.SyncRoot)
            {
                if (latestState.Buffer.Length == 0 || string.IsNullOrWhiteSpace(latestState.Buffer.ToString()))
                {
                    return;
                }

                EnsureBubbleCreated(latestState);
                currentText = latestState.Buffer.ToString();
            }

            if (latestState.Presenter is not null)
            {
                latestState.Presenter.Markdown = currentText;
            }

            // 流式 token 期间自动跟随滚动（尊重用户上滚）
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.AutoScrollToBottom();
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnDoneAsync(string turnId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (ShouldIgnoreTurn(turnId, cancellationToken))
        {
            return Task.CompletedTask;
        }

        _latestTurnId = turnId;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_statesByTurnId.TryGetValue(turnId, out var state))
            {
                return;
            }

            string finalText;
            lock (state.SyncRoot)
            {
                finalText = state.Buffer.ToString();
            }

            // 最终刷新一次确保完整内容（跳过防抖立即渲染）
            if (state.Presenter is not null && !string.IsNullOrEmpty(finalText))
            {
                state.Presenter.FlushRender();
                state.Presenter.Markdown = finalText;
            }

            ScrollToBottom(force: true);

            // 必须在 UI 线程回调内重置，否则 Post 异步导致 _currentPresenter 提前被置空
            ResetTurn(turnId, cancelToolGroups: false, toolCancelReason: string.Empty);
        });

        logger.LogDebug("UI 输出通道完成，Turn：{TurnId}，Session：{SessionId}", turnId, sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnCancelledAsync(string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return Task.CompletedTask;
        }

        MarkTurnCancelled(turnId);
        _latestTurnId = turnId;
        logger.LogDebug("UI 输出通道已取消，Turn：{TurnId}", turnId);
        Dispatcher.UIThread.Post(() => ResetTurn(turnId, cancelToolGroups: true, toolCancelReason: "本轮输出已取消。"));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnErrorAsync(string turnId, string message, CancellationToken cancellationToken = default)
    {
        if (ShouldIgnoreTurn(turnId, cancellationToken))
        {
            return Task.CompletedTask;
        }

        _latestTurnId = turnId;
        Dispatcher.UIThread.Post(() =>
        {
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.AddMessageBubble($"⚠ {message}", isUser: false);
            ResetTurn(turnId, cancelToolGroups: true, toolCancelReason: "本轮输出出错。");
        });

        logger.LogWarning("UI 输出通道收到错误，Turn：{TurnId}，Message：{Message}", turnId, message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 确保已创建 AI 回复气泡。首次收到 token 时创建。
    /// 复用 ChatView 的消息布局，避免流式回复与历史消息出现不同的宽度规则。
    /// </summary>
    private void EnsureBubbleCreated(TurnRenderState state)
    {
        if (state.Presenter is not null)
        {
            return;
        }

        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        state.Presenter = mainWindow.CreateStreamingAssistantBubble();
        ScrollToBottom();
    }

    /// <summary>
    /// 滚动消息列表到底部。
    /// </summary>
    private void ScrollToBottom(bool force = false)
    {
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        if (force)
            mainWindow.ForceScrollToBottom();
        else
            mainWindow.AutoScrollToBottom();
    }

    private void HandleProcessEvent(RealtimeProcessEvent evt)
    {
        var resolvedTurnId = string.IsNullOrWhiteSpace(evt.TurnId) ? _latestTurnId : evt.TurnId;
        if (string.IsNullOrWhiteSpace(resolvedTurnId) || ShouldIgnoreTurn(resolvedTurnId, CancellationToken.None))
        {
            return;
        }

        _latestTurnId = resolvedTurnId;
        var state = _statesByTurnId.GetOrAdd(resolvedTurnId, static _ => new TurnRenderState());
        var normalizedEvent = string.Equals(resolvedTurnId, evt.TurnId, StringComparison.Ordinal)
            ? evt
            : new RealtimeProcessEvent
            {
                TurnId = resolvedTurnId,
                ProcessId = evt.ProcessId,
                Kind = evt.Kind,
                Title = evt.Title,
                Status = evt.Status,
                Content = evt.Content,
                ExitCode = evt.ExitCode,
                DurationMs = evt.DurationMs,
                Timestamp = evt.Timestamp,
            };

        if (IsToolProcessKind(normalizedEvent.Kind))
        {
            HandleToolProcessEvent(state, normalizedEvent);
            return;
        }

        var status = string.IsNullOrWhiteSpace(normalizedEvent.Status) ? "running" : normalizedEvent.Status.Trim().ToLowerInvariant();

        if (status == "running")
        {
            if (!state.CardsByProcessId.TryGetValue(normalizedEvent.ProcessId, out var handle))
            {
                if (!ShouldCreateProcessCard(normalizedEvent))
                {
                    return;
                }

                var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
                handle = mainWindow.AddRealtimeProcessCard(normalizedEvent);
                state.CardsByProcessId[normalizedEvent.ProcessId] = handle;
            }
            else
            {
                handle.UpdateStatus(status, normalizedEvent.ExitCode, normalizedEvent.DurationMs);
                handle.AppendContent(normalizedEvent.Content);
            }

            ScrollToBottom();
            return;
        }

        if (state.CardsByProcessId.TryGetValue(normalizedEvent.ProcessId, out var existing))
        {
            existing.AppendContent(normalizedEvent.Content);
            existing.Complete(status, normalizedEvent.ExitCode, normalizedEvent.DurationMs);
            state.CardsByProcessId.Remove(normalizedEvent.ProcessId);
            ScrollToBottom();
            return;
        }

        if (!ShouldCreateProcessCard(normalizedEvent))
        {
            return;
        }

        var window = serviceProvider.GetRequiredService<MainWindow>();
        var created = window.AddRealtimeProcessCard(new RealtimeProcessEvent
        {
            TurnId = normalizedEvent.TurnId,
            ProcessId = normalizedEvent.ProcessId,
            Kind = normalizedEvent.Kind,
            Title = normalizedEvent.Title,
            Status = "running",
            Content = string.Empty,
            ExitCode = normalizedEvent.ExitCode,
            DurationMs = normalizedEvent.DurationMs,
            Timestamp = normalizedEvent.Timestamp,
        });
        created.AppendContent(normalizedEvent.Content);
        created.Complete(status, normalizedEvent.ExitCode, normalizedEvent.DurationMs);
    }

    private void HandleToolProcessEvent(TurnRenderState state, RealtimeProcessEvent evt)
    {
        if (!HasExistingToolGroup(state, evt) && !ShouldCreateProcessCard(evt))
        {
            return;
        }

        var group = GetOrCreateToolGroup(state, evt);
        group.UpdateTool(evt);
        ScrollToBottom();
    }

    private static bool HasExistingToolGroup(TurnRenderState state, RealtimeProcessEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.TurnId) && state.ToolGroupsByTurnId.ContainsKey(evt.TurnId))
        {
            return true;
        }

        return state.ToolGroupsByProcessId.ContainsKey(evt.ProcessId);
    }

    private ToolProcessGroupCard GetOrCreateToolGroup(TurnRenderState state, RealtimeProcessEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.TurnId))
        {
            if (!state.ToolGroupsByTurnId.TryGetValue(evt.TurnId, out var group))
            {
                var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
                group = mainWindow.AddToolProcessGroupCard(evt.TurnId, GetCurrentToolContentSide(state));
                state.ToolGroupsByTurnId[evt.TurnId] = group;
            }

            state.ToolGroupsByProcessId[evt.ProcessId] = group;
            return group;
        }

        if (!state.ToolGroupsByProcessId.TryGetValue(evt.ProcessId, out var fallbackGroup))
        {
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            fallbackGroup = mainWindow.AddToolProcessGroupCard(evt.ProcessId, GetCurrentToolContentSide(state));
            state.ToolGroupsByProcessId[evt.ProcessId] = fallbackGroup;
        }

        return fallbackGroup;
    }

    private static ChatContentSide GetCurrentToolContentSide(TurnRenderState state)
    {
        return state.Presenter is null
            ? ChatContentSide.User
            : ChatContentSide.Assistant;
    }

    /// <summary>
    /// 重置通道状态，为下一轮对话做准备。
    /// </summary>
    private void ResetTurn(string turnId, bool cancelToolGroups, string toolCancelReason)
    {
        if (!_statesByTurnId.TryRemove(turnId, out var state))
        {
            return;
        }

        foreach (var card in state.CardsByProcessId.Values)
        {
            card.Complete("cancelled", null, 0);
        }

        if (cancelToolGroups)
        {
            CancelToolGroups(state, toolCancelReason);
        }
        else
        {
            CompleteToolGroups(state);
        }

        lock (state.SyncRoot)
        {
            state.Buffer.Clear();
            state.Presenter = null;
            state.CardsByProcessId.Clear();
            state.ToolGroupsByTurnId.Clear();
            state.ToolGroupsByProcessId.Clear();
        }
    }

    private static void CompleteToolGroups(TurnRenderState state)
    {
        foreach (var group in state.ToolGroupsByTurnId.Values.Concat(state.ToolGroupsByProcessId.Values).Distinct())
        {
            group.CompleteIfIdle();
        }
    }

    private static void CancelToolGroups(TurnRenderState state, string reason)
    {
        foreach (var group in state.ToolGroupsByTurnId.Values.Concat(state.ToolGroupsByProcessId.Values).Distinct())
        {
            group.CancelRunningTools(reason);
            group.CompleteIfIdle();
        }
    }

    private bool ShouldIgnoreTurn(string turnId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(turnId))
        {
            return true;
        }

        CleanupCancelledTurns();
        return _cancelledTurnIds.ContainsKey(turnId);
    }

    private void MarkTurnCancelled(string turnId)
    {
        CleanupCancelledTurns();
        _cancelledTurnIds[turnId] = DateTimeOffset.UtcNow;
    }

    private void CleanupCancelledTurns()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _cancelledTurnIds)
        {
            if (now - entry.Value > CancelledTurnRetention)
            {
                _cancelledTurnIds.TryRemove(entry.Key, out _);
            }
        }
    }

    private static bool IsToolProcessKind(string kind)
    {
        return kind.Trim().ToLowerInvariant() is "tool" or "command" or "agent";
    }

    private static bool ShouldCreateProcessCard(RealtimeProcessEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Content))
        {
            return true;
        }

        var status = string.IsNullOrWhiteSpace(evt.Status)
            ? "running"
            : evt.Status.Trim().ToLowerInvariant();

        return status is "failed" or "cancelled"
            || evt.ExitCode is not null and not 0;
    }

    private sealed class TurnRenderState
    {
        public object SyncRoot { get; } = new();
        public StringBuilder Buffer { get; } = new();
        public Dictionary<string, RealtimeProcessCardHandle> CardsByProcessId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ToolProcessGroupCard> ToolGroupsByTurnId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ToolProcessGroupCard> ToolGroupsByProcessId { get; } = new(StringComparer.Ordinal);
        public MarkdownRenderer? Presenter { get; set; }
    }
}
