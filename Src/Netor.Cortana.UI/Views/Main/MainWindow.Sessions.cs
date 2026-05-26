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

            LeftPanelHost.ReloadHistory();

            if (sessions.Count > 0)
            {
                SwitchToSession(sessions[0].Id, sessions[0].Title);
            }
            else
            {
                ChatTabContent.Clear();
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
    /// 刷新当前会话标题（触发左侧 ChatHistoryPanel Reload）。
    /// </summary>
    private void RefreshCurrentSessionTitle()
    {
        var sessionId = LeftPanelHost.CurrentSessionId;
        if (string.IsNullOrEmpty(sessionId)) return;
        try { LeftPanelHost.ReloadHistory(); }
        catch { }
    }

    /// <summary>
    /// 切换到指定会话：加载该会话消息并通知 AiChatService。
    /// </summary>
    private void SwitchToSession(string sessionId, string title)
    {
        LeftPanelHost.CurrentSessionId = sessionId;
        ChatTabContent.Clear();

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
                ChatTabContent.AddMessageBubble(displayContent, isUser, msgAssets, name, msg.CreatedAt);
            }

            ChatTabContent.ScrollToBottomOnNextLayout();
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "切换会话失败: {SessionId}", sessionId);
        }
    }

    /// <summary>构造聊天气泡显示内容。</summary>
    private static string BuildDisplayContent(ChatMessageEntity message)
    {
        var role = message.Role?.ToLowerInvariant() ?? string.Empty;

        // 系统消息不显示
        if (role == "system") return string.Empty;

        // 推理消息单独处理
        if (role == "reasoning") return BuildReasoningDisplayContent(message);

        // 普通文本内容
        var content = message.Content?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(content))
            return content;

        // 回退到 ContentsJson 中的文本部分
        var structured = Netor.Cortana.AI.ChatMessageExtensions.ParseContentsJson(message.ContentsJson);
        if (structured is not { Count: > 0 }) return string.Empty;

        var textParts = structured
            .OfType<TextContent>()
            .Select(static t => t.Text?.Trim())
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return textParts.Count == 0 ? string.Empty : string.Join("\n\n", textParts);
    }

    private static string BuildReasoningDisplayContent(ChatMessageEntity message)
    {
        var structured = Netor.Cortana.AI.ChatMessageExtensions.ParseContentsJson(message.ContentsJson);
        if (structured is not { Count: > 0 }) return string.Empty;

        var reasoningParts = structured
            .OfType<TextReasoningContent>()
            .Select(static r => r.Text?.Trim())
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return reasoningParts.Count == 0 ? string.Empty : string.Join("\n\n", reasoningParts);
    }

    /// <summary>显示欢迎面板。</summary>
    private void ShowWelcome() => ChatTabContent.ShowWelcome();

    /// <summary>隐藏欢迎面板。</summary>
    private void HideWelcome() => ChatTabContent.HideWelcome();

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

    /// <summary>
    /// 新建会话按钮点击。
    /// - 专家模式 → 新建 Chat 会话
    /// - 工作模式 → 重置 WorkflowInputVm 状态（清空 CurrentTaskId），回到"空任务选中"界面
    /// - 会议模式 → 重置 GroupChatInputVm 状态（清空 CurrentTaskId），回到"空任务选中"界面
    /// </summary>
    private async void OnNewSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        switch (_currentTab)
        {
            case "chat":
                await chatEngine.NewSessionAsync();
                break;

            case "workflow":
            {
                var wVm = App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Workspace.WorkflowInputVm>();
                wVm.OnTaskFinished();         // 清空 CurrentTaskId + IsRunning=false
                _workspaceTabVm.Detail.Clear(); // 清空详情区，回到空状态提示
                _workspaceTabVm.List.SelectedItem = null; // 取消列表选中
                break;
            }

            case "groupchat":
            {
                var gcVm = App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Workspace.GroupChatInputVm>();
                gcVm.OnTaskFinished();
                _workspaceTabVm.Detail.Clear();
                _workspaceTabVm.List.SelectedItem = null;
                break;
            }
        }
    }

    /// <summary>清空页面消息按钮点击（仅清空 UI，不删除数据库记录）。</summary>
    private void OnClearMessagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChatTabContent.Clear();
    }
}
