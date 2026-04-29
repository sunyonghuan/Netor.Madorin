using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;

namespace Netor.Cortana.AvaloniaUI.Views;

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

            FillHistoryList(sessions);

            // 自动加载最近的会话消息
            if (sessions.Count > 0)
            {
                SwitchToSession(sessions[0].Id, sessions[0].Title);
            }
            else
            {
                MessageList.Items.Clear();
                HistoryLabel.Text = "新对话";
                ShowWelcome();
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
    /// 填充会话历史列表到 Popup。
    /// </summary>
    private void FillHistoryList(List<ChatSessionEntity> sessions)
    {
        HistoryList.Items.Clear();

        foreach (var session in sessions.Take(15))
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? "新对话" : session.Title;
            var btn = new Button
            {
                Classes = { "selector-item" },
                Tag = session.Id,
                Content = new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    MaxWidth = 220,
                },
            };
            btn.Click += OnHistoryItemClick;
            HistoryList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 从数据库重新读取当前会话标题，刷新顶部标签和历史列表中对应项。
    /// </summary>
    private void RefreshCurrentSessionTitle()
    {
        var sessionId = HistoryPanel.CurrentSessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        try
        {
            var db = App.Services.GetRequiredService<CortanaDbContext>();
            var session = db.QueryFirstOrDefault(
                "SELECT * FROM ChatSessions WHERE Id = @Id",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", sessionId));
            if (session is null) return;

            var title = string.IsNullOrWhiteSpace(session.Title) ? "新对话" : session.Title;
            HistoryLabel.Text = title;

            // 同步更新 Popup 历史列表中对应按钮的文本
            foreach (var item in HistoryList.Items)
            {
                if (item is Button btn && btn.Tag is string id
                    && string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)
                    && btn.Content is TextBlock tb)
                {
                    tb.Text = title;
                    break;
                }
            }
        }
        catch { /* 非关键路径，静默忽略 */ }
    }

    /// <summary>
    /// 切换到指定会话：加载该会话消息并通知 AiChatService。
    /// </summary>
    private void SwitchToSession(string sessionId, string title)
    {
        HistoryLabel.Text = string.IsNullOrWhiteSpace(title) ? "新对话" : title;
        HistoryPanel.CurrentSessionId = sessionId;
        MessageList.Items.Clear();

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
                if (string.IsNullOrWhiteSpace(msg.Content))
                    continue;

                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);
                assetsByMessage.TryGetValue(msg.Id, out var msgAssets);
                var name = !string.IsNullOrWhiteSpace(msg.AuthorName) ? msg.AuthorName : null;
                AddMessageBubble(msg.Content, isUser, msgAssets, name, msg.CreatedAt);
            }

            // 加载完消息后强制滚动到底部（等待布局完成后执行）
            _userScrolledUp = false;
            MessageList.LayoutUpdated += ScrollOnceAfterLayout;

            void ScrollOnceAfterLayout(object? s, System.EventArgs e2)
            {
                MessageList.LayoutUpdated -= ScrollOnceAfterLayout;
                Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
            }

            // 通知 AiChatService 恢复该会话上下文
            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            _ = Task.Run(() => chatService.ResumeSessionAsync(sessionId));
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "切换会话失败: {SessionId}", sessionId);
        }
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

    // ──────── 历史 Popup & 会话操作按钮 ────────

    /// <summary>会话历史下拉按钮点击 → 弹出/关闭 Popup。</summary>
    private void OnHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
    }

    /// <summary>会话历史列表项点击 → 切换到选中的会话。</summary>
    private void OnHistoryItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string sessionId)
        {
            var title = btn.Content is TextBlock tb ? tb.Text ?? "新对话" : "新对话";
            HistoryPopup.IsOpen = false;
            SwitchToSession(sessionId, title);
        }
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
