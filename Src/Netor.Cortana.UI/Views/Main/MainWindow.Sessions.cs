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
        _ = CreateChatSessionSafelyAsync();
    }

    /// <summary>
    /// 加载会话历史列表并自动恢复最近一个会话。
    /// </summary>
    private void LoadSessions()
    {
        try
        {
            var sessionService = App.Services.GetRequiredService<ChatSessionService>();
            var categorize = App.WorkspaceDirectory.Md5Encrypt();
            var sessions = sessionService.GetVisibleExpertSessions(categorize, int.MaxValue);

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
        var categorize = App.WorkspaceDirectory.Md5Encrypt();
        var sessionService = App.Services.GetRequiredService<ChatSessionService>();
        if (!sessionService.IsVisibleExpertSession(sessionId, categorize))
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogWarning("已拒绝恢复非专家模式会话：{SessionId}", sessionId);
            LeftPanelHost.CurrentSessionId = string.Empty;
            ChatTabContent.Clear();
            ShowWelcome();
            return;
        }

        LeftPanelHost.CurrentSessionId = sessionId;
        ChatTabContent.Clear();
        ChatTabContent.RefreshOrchestrationDiagnostics();

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

            var appPaths = App.Services.GetRequiredService<IAppPaths>();

            foreach (var msg in messages)
            {
                var displayContent = BuildDisplayContent(msg);
                if (string.IsNullOrWhiteSpace(displayContent))
                    continue;

                bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);
                assetsByMessage.TryGetValue(msg.Id, out var msgAssets);

                // 历史恢复时：将图片资源重建为 Markdown 内联预览
                // （发送时 AiChatHostedService 注入了 ![name](path)，但 Content 持久化可能只保留文本部分）
                if (msgAssets is { Count: > 0 })
                {
                    var imageMarkdown = string.Join("\n", msgAssets
                        .Where(a => a.AssetGroup == "images" && !string.IsNullOrWhiteSpace(a.RelativePath))
                        .Select(a =>
                        {
                            var absPath = Path.Combine(appPaths.WorkspaceResourcesDirectory, a.RelativePath);
                            return $"![{a.OriginalName}]({absPath})";
                        }));

                    if (!string.IsNullOrWhiteSpace(imageMarkdown) &&
                        !displayContent.Contains("![", StringComparison.Ordinal))
                    {
                        displayContent = string.IsNullOrWhiteSpace(displayContent)
                            ? imageMarkdown
                            : $"{displayContent}\n{imageMarkdown}";
                    }
                }

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

    /// <summary>
    /// 新建会话按钮点击。
    /// </summary>
    private async void OnNewSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        switch (_currentTab)
        {
            case "chat":
                await CreateChatSessionSafelyAsync();
                break;

            case "workflow":
                await CreateWorkflowSessionSafelyAsync();
                break;

            case "groupchat":
                MeetingTabContent.StartNewMeeting();
                break;
        }
    }

    /// <summary>
    /// 安全创建新聊天会话，避免后台异常在 AOT/异步路径中直接终止进程。
    /// </summary>
    private async Task CreateChatSessionSafelyAsync()
    {
        try
        {
            await chatEngine.NewSessionAsync();
            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.Clear();
                ShowWelcome();
                ChatTabContent.RefreshOrchestrationDiagnostics();
                LeftPanelHost.ReloadHistory();
            });
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "新建会话失败");
            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                ChatTabContent.AddSystemNotice(new SystemNoticeArgs(
                    $"新建会话失败：{ex.Message}",
                    "新建会话失败",
                    "error",
                    "系统",
                    DateTimeOffset.Now));
            });
        }
    }

    /// <summary>
    /// 为工作模式创建新的底层会话，并重置工作视图，避免继续选中的历史任务或当前会话活跃任务。
    /// </summary>
    private async Task CreateWorkflowSessionSafelyAsync()
    {
        try
        {
            await chatEngine.NewSessionAsync();
            Dispatcher.UIThread.Post(() =>
            {
                WorkflowTabContent.StartNewWork();
                LeftPanelHost.ReloadHistory();
            });
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "新建工作失败");
            Dispatcher.UIThread.Post(() =>
            {
                WorkflowTabContent.ShowNotice(
                    $"新建工作失败：{ex.Message}",
                    "新建工作失败",
                    "error");
            });
        }
    }

    /// <summary>清空页面消息按钮点击（仅清空 UI，不删除数据库记录）。</summary>
    private void OnClearMessagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChatTabContent.Clear();
    }
}
