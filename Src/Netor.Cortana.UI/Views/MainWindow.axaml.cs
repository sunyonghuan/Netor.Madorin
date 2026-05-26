using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls;
using Netor.Cortana.UI.Models;
using Netor.EventHub;

using System.Globalization;

namespace Netor.Cortana.UI.Views;

/// <summary>
/// 主对话窗口（重构版，2026-05-26）。
///
/// 重构变更：
/// - Chat 输入区、消息列表、附件、走马灯、选择器等全部迁移到 ChatView / InputAreaView。
/// - 本文件只保留：窗口生命周期、EventHub 订阅、Tab 切换、左侧面板初始化。
///
/// 职责拆分：
/// <list type="bullet">
/// <item><c>MainWindow.Sessions.cs</c>：会话历史加载 / 切换 / 欢迎面板。</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private const string PlacementXKey = "UI.MainWindow.X";
    private const string PlacementYKey = "UI.MainWindow.Y";
    private const string PlacementWidthKey = "UI.MainWindow.Width";
    private const string PlacementHeightKey = "UI.MainWindow.Height";

    private ISubscriber? _subscriber;
    private bool _forceClose;
    private bool _workspaceOpen;

#if DEBUG
    private bool _debugSystemNoticeShown;
#endif

    // 阶段 5B Phase 3：当前展示中的 Workflow 建议数据
    private string? _pendingSuggestionInput;
    private string? _pendingSuggestionSubMode;

    private readonly IAiChatEngine chatEngine = App.Services.GetRequiredService<IAiChatEngine>();

    // ──── C2：主窗口 ViewModel + DraftService ────

    private readonly Netor.Cortana.UI.ViewModels.MainWindowVm _mainVm =
        App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.MainWindowVm>();

    private readonly Netor.Cortana.UI.Services.ChatDraftService _draftService =
        App.Services.GetRequiredService<Netor.Cortana.UI.Services.ChatDraftService>();

    /// <summary>工作台 VM（与 TaskListPanel + WorkflowDetailView 共享同一 DI Singleton 实例）。</summary>
    private readonly Netor.Cortana.UI.ViewModels.Workspace.WorkspaceTabVm _workspaceTabVm =
        App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Workspace.WorkspaceTabVm>();

    private static readonly IReadOnlyList<string> WorkflowSubModes = ["magentic", "parallelanalysis"];
    private static readonly IReadOnlyList<string> GroupChatSubModes = ["groupchat"];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private void OnWindowLoaded(object? sender, System.EventArgs e)
    {
        SubscribeEvents();
        LoadInitialData();

        // 左侧面板初始化
        LeftPanelHost.DataContext = App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.LeftPanelVm>();
        LeftPanelHost.WorkspaceDirectory = App.WorkspaceDirectory;
        LeftPanelHost.AttachmentRequested += OnWorkspaceAttachmentRequested;
        LeftPanelHost.WorkflowAttachmentRequested += paths => WorkflowTabContent.AddExternalAttachments(paths);
        LeftPanelHost.GroupChatAttachmentRequested += paths => GroupChatTabContent.AddExternalAttachments(paths);

        LeftPanelHost.SessionSelected += OnHistoryPanelSessionSelected;
        LeftPanelHost.RequestNewSession += OnHistoryPanelRequestNewSession;
        LeftPanelHost.AttachHistoryScrollHandler();

        // Token 使用量变更 → 刷新（通过 ChatView 的 RefreshTokenProgress）
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        factory.TokenUsageChanged += RefreshTokenProgress;
        RefreshTokenProgress();

        // ChatView：WorkflowSuggestion Banner 事件
        ChatTabContent.WorkflowSuggestionAccepted += OnChatViewWorkflowSuggestionAccepted;
        ChatTabContent.WorkflowSuggestionDismissed += OnChatViewWorkflowSuggestionDismissed;

        // 启动时按 _mainVm.CurrentMode 恢复 UI 状态
        if (_mainVm.CurrentMode != WorkMode.Chat)
        {
            ApplyModeToUI(_mainVm.CurrentMode);
            if (_mainVm.CurrentMode == WorkMode.Workflow)
                _ = _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty, WorkflowSubModes);
            else if (_mainVm.CurrentMode == WorkMode.GroupChat)
                _ = _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty, GroupChatSubModes);
        }

        ShowDebugSystemNotice();
    }

    private void ShowDebugSystemNotice()
    {
#if DEBUG
        if (_debugSystemNoticeShown) return;
        _debugSystemNoticeShown = true;
        ChatTabContent.HideWelcome();
        ChatTabContent.AddSystemNotice(new SystemNoticeArgs(
            "这是调试模式下用于验收 system.notice 样式的系统提醒。\n当前默认只预览两行内容。\n点击标题左侧图标可以展开完整详情。\n再次点击可以折叠回预览状态。",
            "系统提醒样式验收",
            "info",
            "调试模式",
            DateTimeOffset.Now));
#endif
    }

    /// <summary>加载初始数据（会话历史）。</summary>
    private void LoadInitialData()
    {
        LoadSessions();
    }

    // ──────── 工作台切换 ────────

    private void OnWorkspaceToggleClick(object? sender, RoutedEventArgs e)
    {
        _workspaceOpen = !_workspaceOpen;
        var col = WorkspaceGrid.ColumnDefinitions[0];
        if (_workspaceOpen)
        {
            col.Width = new GridLength(280);
            col.MinWidth = 180;
            WorkspaceSplitter.IsVisible = true;
        }
        else
        {
            col.MinWidth = 0;
            col.Width = new GridLength(0);
            WorkspaceSplitter.IsVisible = false;
        }
    }

    // ──────── EventHub 订阅 ────────

    private void SubscribeEvents()
    {
        _subscriber = App.Services.GetRequiredService<ISubscriber>();

        // 用户发送消息 → 显示用户气泡（InputBox 发送路径）
        _subscriber.Subscribe<ConversationUserMessageArgs>(Events.OnConversationUserMessage, (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Content) && args.Attachments.Count == 0)
                return Task.FromResult(false);

            // 构建显示文本：正文 + 附件名列表
            var attachmentNames = args.Attachments.Count > 0
                ? string.Join(", ", args.Attachments.Select(a => $"📎 {a.Name}"))
                : string.Empty;
            var displayText = string.IsNullOrWhiteSpace(attachmentNames)
                ? args.Content
                : string.IsNullOrWhiteSpace(args.Content)
                    ? attachmentNames
                    : $"{args.Content}\n{attachmentNames}";

            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                ChatTabContent.AddMessageBubble(displayText, isUser: true);
            });
            return Task.FromResult(false);
        });

        // AI 推理开始 → ChatView 已通过 ChatInputVm 自动处理走马灯，这里只做 Token 刷新
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiStarted, (_, _) =>
        {
            // ChatInputVm 已订阅 OnAiStarted → IsRunning=true → InputAreaView 走马灯启动
            return Task.FromResult(false);
        });

        // AI 推理完成 → 刷新会话标题
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiCompleted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                RefreshCurrentSessionTitle();
                RefreshTokenProgress();
            });
            return Task.FromResult(false);
        });

        // AI 配置变更 → ChatInputVm.LoadAvailableAgents/Providers 已自动重载（监听 OnAgentChange 等）
        // 此处保留 Selectors 刷新以兼容旧 ChatService 路径
        _subscriber.Subscribe<DataChangeArgs>(Events.OnAiProviderChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var chatInputVm = App.Services.GetRequiredService<ViewModels.Chat.ChatInputVm>();
                chatInputVm.LoadAvailableProviders();
                if (chatInputVm.SelectedProvider is not null)
                    chatInputVm.LoadModelsForProvider(chatInputVm.SelectedProvider.Id);
            });
            return Task.FromResult(false);
        });

        _subscriber.On(Events.OnAgentChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var chatInputVm = App.Services.GetRequiredService<ViewModels.Chat.ChatInputVm>();
                chatInputVm.LoadAvailableAgents();
            });
            return Task.FromResult(false);
        });

        // AI 生成会话标题完成 → 刷新左侧列表
        _subscriber.Subscribe<SessionTitleUpdatedArgs>(Events.OnSessionTitleUpdated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => RefreshCurrentSessionTitle());
            return Task.FromResult(false);
        });

        // 语音识别最终结果 → 显示用户消息气泡
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Text)) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                ChatTabContent.AddMessageBubble(args.Text, isUser: true);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WebSocketUserMessageReceivedArgs>(Events.OnWebSocketUserMessageReceived, (_, args) =>
        {
            var attachmentNames = args.Attachments.Count > 0
                ? string.Join(", ", args.Attachments.Select(a => $"📎 {a.Name}"))
                : string.Empty;
            var displayText = string.IsNullOrWhiteSpace(attachmentNames)
                ? args.Text
                : string.IsNullOrWhiteSpace(args.Text)
                    ? attachmentNames
                    : $"{args.Text}\n{attachmentNames}";
            if (string.IsNullOrWhiteSpace(displayText)) return Task.FromResult(false);

            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                ChatTabContent.AddMessageBubble(displayText, isUser: true);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<SystemNoticeArgs>(Events.OnSystemNotice, (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Content)) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                ChatTabContent.AddSystemNotice(args);
            });
            return Task.FromResult(false);
        });

        // 工作目录变更 → 刷新文件树 + 重载会话
        _subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                LeftPanelHost.WorkspaceDirectory = args.Path;
                LoadSessions();
                LeftPanelHost.ReloadHistory();
            });
            return Task.FromResult(false);
        });

        // 新会话已创建 → 刷新列表 + 切换到新会话
        _subscriber.Subscribe<SessionCreatedArgs>(Events.OnSessionCreated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.Clear();
                LoadSessions();
                SwitchToSession(args.SessionId, "新对话");
            });
            return Task.FromResult(false);
        });

        // Chat→Workflow 建议事件
        _subscriber.Subscribe<WorkflowSuggestionArgs>(Events.OnWorkflowSuggestion, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _pendingSuggestionInput = args.OriginalInput;
                _pendingSuggestionSubMode = args.SuggestedSubMode;

                var preview = args.OriginalInput.Length > 80
                    ? args.OriginalInput[..80] + "…"
                    : args.OriginalInput;
                ChatTabContent.ShowWorkflowSuggestion(args.Reason, preview, args.SuggestedSubMode, args.OriginalInput);
            });
            return Task.FromResult(false);
        });
    }

    // ──────── Token 进度条 ────────

    private void RefreshTokenProgress()
    {
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        var max = factory.MaxContextTokens;
        if (max <= 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            ChatTabContent.RefreshTokenProgress(factory.LastInputTokens, max);
        });
    }

    // ──────── 工作区附件回调 ────────

    private void OnWorkspaceAttachmentRequested(IReadOnlyList<string> filePaths)
    {
        // Chat 模式下通过 ChatView 的 InputAreaView 注入附件
        ChatTabContent.AddExternalAttachments(filePaths);
    }

    // ──────── ChatView WorkflowSuggestion 事件 ────────

    private async void OnChatViewWorkflowSuggestionAccepted(string? pendingInput, string? subMode)
    {
        var input = pendingInput ?? _pendingSuggestionInput ?? string.Empty;
        var mode = subMode ?? _pendingSuggestionSubMode ?? "Magentic";
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;

        try
        {
            if (_currentTab != "workflow")
            {
                ApplyModeToUI(WorkMode.Workflow);
                _mainVm.CurrentMode = WorkMode.Workflow;
                try { await _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty, WorkflowSubModes); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] WorkflowTab attach error: {ex.Message}"); }
            }

            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            var options = new TaskStartOptions { SubMode = mode };
            var taskId = await engine.StartTaskAsync(input, workspaceId: string.Empty, templateId: null, options, CancellationToken.None);
            System.Diagnostics.Debug.WriteLine($"[MainWindow] WorkflowSuggestionAccepted: taskId={taskId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] WorkflowSuggestionAccepted error: {ex.Message}");
        }
    }

    private void OnChatViewWorkflowSuggestionDismissed()
    {
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;
    }

    // ──────── 设置 / 关闭 ────────

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsWindow>();
        settings.Show();
        settings.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_forceClose && !App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
    }

    internal void ApplySavedPlacement()
    {
        var settings = App.Services.GetRequiredService<SystemSettingsService>();
        var x = ReadInt(settings, PlacementXKey);
        var y = ReadInt(settings, PlacementYKey);
        var width = ReadDouble(settings, PlacementWidthKey);
        var height = ReadDouble(settings, PlacementHeightKey);
        if (x is null || y is null || width is null || height is null) return;

        Width = Math.Max(MinWidth, width.Value);
        Height = Math.Max(MinHeight, height.Value);
        var position = new PixelPoint(x.Value, y.Value);
        if (IsPositionVisible(position))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }
    }

    internal void SaveCurrentPlacement()
    {
        if (WindowState != WindowState.Normal) return;
        var settings = App.Services.GetRequiredService<SystemSettingsService>();
        settings.SetValue(PlacementXKey, Position.X.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementYKey, Position.Y.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementWidthKey, Width.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementHeightKey, Height.ToString(CultureInfo.InvariantCulture));
    }

    private bool IsPositionVisible(PixelPoint position)
    {
        foreach (var screen in Screens.All)
        {
            var area = screen.WorkingArea;
            if (position.X >= area.X && position.X < area.X + area.Width
                && position.Y >= area.Y && position.Y < area.Y + area.Height)
                return true;
        }
        return false;
    }

    private static int? ReadInt(SystemSettingsService settings, string key)
        => int.TryParse(settings.GetValue(key), CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ReadDouble(SystemSettingsService settings, string key)
        => double.TryParse(settings.GetValue(key), CultureInfo.InvariantCulture, out var value) ? value : null;

    internal void ForceClose()
    {
        _forceClose = true;
        _subscriber?.Dispose();
        try
        {
            var factory = App.Services.GetRequiredService<AIAgentFactory>();
            factory.TokenUsageChanged -= RefreshTokenProgress;
        }
        catch { }
        Close();
    }

    // ──────── Tab 切换 ────────

    private string _currentTab = "chat";

    private void ApplyModeToUI(WorkMode mode)
    {
        var toChat = mode == WorkMode.Chat;
        var toWorkflow = mode == WorkMode.Workflow;
        var toGroupChat = mode == WorkMode.GroupChat;

        ChatTabContent.IsVisible = toChat;
        WorkflowTabContent.IsVisible = toWorkflow;
        GroupChatTabContent.IsVisible = toGroupChat;

        ChatTabButton.Classes.Set("tab-btn-active", toChat);
        WorkflowTabButton.Classes.Set("tab-btn-active", toWorkflow);
        GroupChatTabButton.Classes.Set("tab-btn-active", toGroupChat);

        _currentTab = mode.ToPersistenceString();
    }

    private async void OnTabSwitchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        if (_currentTab == tab) return;

        var targetMode = WorkModeExtensions.FromPersistenceString(tab);

        // C2 守卫：从 Chat → Workflow/GroupChat 且有未保存内容时弹确认对话框
        if (_currentTab == "chat" && targetMode != WorkMode.Chat)
        {
            var chatInputVm = App.Services.GetRequiredService<ViewModels.Chat.ChatInputVm>();
            var hasText = !string.IsNullOrWhiteSpace(chatInputVm.InitialInput);
            var hasAttachments = chatInputVm.Attachments.Count > 0;
            if (hasText || hasAttachments)
            {
                try
                {
                    var preview = hasText ? chatInputVm.InitialInput : string.Empty;
                    if (preview.Length > 50) preview = string.Concat(preview.AsSpan(0, 50), "…");

                    var choice = await Views.Dialogs.UnsavedChangesDialog
                        .ShowDialogAsync(this, preview, chatInputVm.Attachments.Count);

                    switch (choice)
                    {
                        case Views.Dialogs.UnsavedChoice.Cancel:
                            return;

                        case Views.Dialogs.UnsavedChoice.Save:
                            _draftService.Save(chatInputVm.InitialInput, [.. chatInputVm.Attachments]);
                            break;

                        case Views.Dialogs.UnsavedChoice.Discard:
                            chatInputVm.InitialInput = string.Empty;
                            chatInputVm.Attachments.Clear();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UnsavedChangesDialog 异常：{ex.Message}");
                    return;
                }
            }
        }

        ApplyModeToUI(targetMode);
        _mainVm.CurrentMode = targetMode;

        if (targetMode == WorkMode.Workflow)
        {
            try { await _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty, WorkflowSubModes); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] WorkflowTab attach error: {ex.Message}"); }
        }
        else if (targetMode == WorkMode.GroupChat)
        {
            try { await _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty, GroupChatSubModes); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] GroupChatTab attach error: {ex.Message}"); }
        }
    }

    // ──────── 代理方法（供 UiChatOutputChannel / App.axaml.cs 调用） ────────

    /// <summary>代理 → ChatTabContent.AddMessageBubble（UiChatOutputChannel 调用）。</summary>
    internal void AddMessageBubble(string content, bool isUser,
        IReadOnlyList<ChatMessageAssetEntity>? assets = null,
        string? authorName = null, DateTimeOffset? timestamp = null)
        => ChatTabContent.AddMessageBubble(content, isUser, assets, authorName, timestamp);

    /// <summary>代理 → ChatTabContent.AddSystemNotice（App.axaml.cs 调用）。</summary>
    internal void AddSystemNotice(SystemNoticeArgs args)
        => ChatTabContent.AddSystemNotice(args);

    /// <summary>代理 → ChatTabContent.AutoScrollToBottom（UiChatOutputChannel 调用）。</summary>
    internal void AutoScrollToBottom()
        => ChatTabContent.AutoScrollToBottom();

    /// <summary>代理 → ChatTabContent.ForceScrollToBottom（UiChatOutputChannel 调用）。</summary>
    internal void ForceScrollToBottom()
        => ChatTabContent.ForceScrollToBottom();

    /// <summary>代理 → ChatTabContent.AddRealtimeProcessCard（UiChatOutputChannel 调用）。</summary>
    internal RealtimeProcessCardHandle AddRealtimeProcessCard(RealtimeProcessEvent initial)
        => ChatTabContent.AddRealtimeProcessCard(initial);

    /// <summary>
    /// 供 UiChatOutputChannel.EnsureBubbleCreated 直接访问消息列表（流式 AI 气泡追加）。
    /// </summary>
    internal ItemsControl ChatMessageList => ChatTabContent.Messages;

    /// <summary>
    /// 供 UiChatOutputChannel.EnsureBubbleCreated 读取当前智能体名称（气泡头部显示）。
    /// </summary>
    internal string CurrentAgentName => ChatTabContent.CurrentAgentName;
}
