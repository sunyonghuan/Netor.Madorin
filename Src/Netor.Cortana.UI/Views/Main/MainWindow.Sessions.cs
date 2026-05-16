using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.AI;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;

using Microsoft.Extensions.AI;

namespace Netor.Cortana.UI.Views;

/// <summary>
/// MainWindow — 会话历史管理（加载 / 切换 / 标题刷新 / 清空）。
/// </summary>
public partial class MainWindow
{
    // ──────── 会话历史管理 ────────

    private void OnHistoryPanelSessionSelected(string sessionId, string title)
    {
        SwitchToSession(sessionId, title);
    }

    private void OnHistoryPanelRequestNewSession()
    {
        Task.Run(() => chatEngine.NewSessionAsync());
    }

    /// <summary>
    /// 加载会话历史列表并自动恢复最近一个会话。
    /// C5 决策 R2：右侧抽屉 Popup 删除后，会话列表由 LeftPanel.Tab2 内的 ChatHistoryPanel 自管理。
    /// 本方法仍负责"自动恢复最近会话"逻辑（DB 查询 + SwitchToSession），但不再填充本地 HistoryList。
    /// </summary>
    private void LoadSessions()
    {
        try
        {
            var db = App.Services.GetRequiredService<CortanaDbContext>();
            var categorize = App.WorkspaceDirectory.Md5Encrypt();
            var sessions = db.Query(
                "SELECT * FROM ChatSessions WHERE IsArchived = 0 AND Categorize = @cat ORDER BY IsPinned DESC, LastActiveTimestamp DESC",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@cat", categorize));

            // C5 决策 R2：通知左侧 ChatHistoryPanel 重新加载（替代原 FillHistoryList 填充 Popup）。
            LeftPanelHost.ReloadHistory();

            // 自动加载最近的会话消息
            if (sessions.Count > 0)
            {
                SwitchToSession(sessions[0].Id, sessions[0].Title);
            }
            else
            {
                MessageList.Items.Clear();
                // C5 决策 R1：HistoryLabel 已删除（顶栏 "最近 ▼" 按钮被砍）。新会话标题不再单独标记。
                ShowWelcome();
                _ = Task.Run(() => chatEngine.NewSessionAsync());
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载会话历史失败");
            ShowWelcome();
        }
    }
    /// <summary>
    /// 从数据库重新读取当前会话标题，触发左侧 ChatHistoryPanel 刷新对应项。
    /// C5 决策 R1+R2：HistoryLabel + HistoryList Popup 已删除（顶栏 "最近 ▼" 被砍），
    /// 标题刷新由 LeftPanel.Tab2 内 ChatHistoryPanel 的列表自然刷新接管。
    /// 本方法保留是为了让 OnSessionTitleUpdated 事件能触发左侧列表 Reload（保持事件链完整）。
    /// </summary>
    private void RefreshCurrentSessionTitle()
    {
        var sessionId = LeftPanelHost.CurrentSessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        // C5 决策：原读取 DB 单条 + 更新 HistoryLabel/HistoryList 逻辑全部删除。
        // 改为直接通知左侧 ChatHistoryPanel 整体 Reload，让其自己从 DB 重新加载列表。
        try
        {
            LeftPanelHost.ReloadHistory();
        }
        catch { /* 非关键路径，静默忽略 */ }
    }

    /// <summary>
    /// 切换到指定会话：加载该会话消息并通知 AiChatService。
    /// </summary>
    private void SwitchToSession(string sessionId, string title)
    {
        // C5 决策 R1：HistoryLabel.Text 赋值已删除（顶栏 "最近 ▼" 按钮被砍）。
        // 当前会话标题由 LeftPanel.Tab2 ChatHistoryPanel 内部高亮处理（CurrentSessionId 比对）。
        LeftPanelHost.CurrentSessionId = sessionId;
        MessageList.Items.Clear();

        // 先通知 AiChatService 恢复该会话上下文。即使该会话还没有消息，也必须绑定，
        // 否则用户在空会话里发送的第一条消息可能落到其他会话或无法形成持久化会话。
        var chatService = App.Services.GetRequiredService<AiChatHostedService>();
        _ = Task.Run(() => chatService.ResumeSessionAsync(sessionId));

        try
        {
            var messageService = App.Services.GetRequiredService<ChatMessageService>();
            var messages = messageService.GetBySessionId(sessionId);

            if (messages.Count == 0)
            {
                ShowWelcome();
                return;
            }

            HideWelcome();

            // 加载消息关联的资源索引，按 MessageId 分组
            var assetService = App.Services.GetRequiredService<ChatMessageAssetService>();
            var allAssets = assetService.GetBySessionId(sessionId);
            var assetsByMessage = new Dictionary<string, List<ChatMessageAssetEntity>>();
            foreach (var asset in allAssets)
            {
                if (!assetsByMessage.TryGetValue(asset.MessageId, out var list))
                {
                    list = [];
                    assetsByMessage[asset.MessageId] = list;
                }
                list.Add(asset);
            }

            foreach (var msg in messages)
            {
                var displayContent = BuildDisplayContent(msg);
                if (string.IsNullOrWhiteSpace(displayContent))
                    continue;

                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);
                assetsByMessage.TryGetValue(msg.Id, out var msgAssets);
                var name = !string.IsNullOrWhiteSpace(msg.AuthorName) ? msg.AuthorName : null;
                AddMessageBubble(displayContent, isUser, msgAssets, name, msg.CreatedAt);
            }

            // 加载完消息后强制滚动到底部（等待布局完成后执行）
            _userScrolledUp = false;
            MessageList.LayoutUpdated += ScrollOnceAfterLayout;

            void ScrollOnceAfterLayout(object? s, System.EventArgs e2)
            {
                MessageList.LayoutUpdated -= ScrollOnceAfterLayout;
                Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "切换会话失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 构造聊天气泡显示内容。reasoning 只保留在结构化历史和 AI 上下文中，不直接渲染到聊天气泡。
    /// </summary>
    private static string BuildDisplayContent(ChatMessageEntity message)
    {
        var structured = Netor.Cortana.AI.ChatMessageExtensions.ParseContentsJson(message.ContentsJson);
        if (structured is { Count: > 0 })
        {
            var textParts = structured
                .OfType<TextContent>()
                .Select(static text => text.Text?.Trim())
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (textParts.Count > 0)
            {
                return string.Join("\n\n", textParts);
            }

            if (structured.Any(static content => content is TextReasoningContent))
            {
                return string.Empty;
            }
        }

        return message.Content ?? string.Empty;
    }

    /// <summary>
    /// 预留：后续如需单独显示思考过程，可从结构化历史中提取 reasoning 内容。
    /// </summary>
    private static string BuildReasoningDisplayContent(ChatMessageEntity message)
    {
        var structured = Netor.Cortana.AI.ChatMessageExtensions.ParseContentsJson(message.ContentsJson);
        if (structured is not { Count: > 0 }) return string.Empty;

        var reasoningParts = structured
            .OfType<TextReasoningContent>()
            .Select(static reasoning => reasoning.Text?.Trim())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return reasoningParts.Count == 0 ? string.Empty : string.Join("\n\n", reasoningParts);
    }

    /// <summary>
    /// 显示欢迎面板，隐藏消息区。
    /// </summary>
    private void ShowWelcome()
    {
        WelcomePanel.IsVisible = true;
    }

    /// <summary>
    /// 隐藏欢迎面板。
    /// </summary>
    private void HideWelcome()
    {
        WelcomePanel.IsVisible = false;
    }

    private static ChatSessionEntity ReadSessionEntity(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new ChatSessionEntity
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Categorize = r.GetString(r.GetOrdinal("Categorize")),
            Title = r.GetString(r.GetOrdinal("Title")),
            Summary = r.GetString(r.GetOrdinal("Summary")),
            RawDiscription = r.GetString(r.GetOrdinal("RawDiscription")),
            AgentId = r.GetString(r.GetOrdinal("AgentId")),
            IsArchived = r.GetInt64(r.GetOrdinal("IsArchived")) != 0,
            IsPinned = r.GetInt64(r.GetOrdinal("IsPinned")) != 0,
            LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveTimestamp")),
            TotalTokenCount = r.GetInt32(r.GetOrdinal("TotalTokenCount"))
        };
    }

    /// <summary>新建会话按钮点击。</summary>
    private async void OnNewSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await chatEngine.NewSessionAsync();
    }

    /// <summary>清空页面消息按钮点击（仅清空 UI 显示，不删除数据库记录）。</summary>
    private void OnClearMessagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MessageList.Items.Clear();
        ShowWelcome();
    }
}
