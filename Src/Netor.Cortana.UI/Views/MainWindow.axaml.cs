using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.Handoff;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls.Common;
using Netor.Cortana.UI.Controls.ExpertMode;
using Netor.Cortana.UI.Controls.Shared;
using Netor.Cortana.UI.Models.WorkMode;
using Netor.Cortana.UI.Views.Proxy;
using Netor.Cortana.Store.Views;
using Netor.Cortana.Voice;
using Netor.EventHub;

using System.Diagnostics;
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
public partial class MainWindow : Window, IWorkModeSwitcher
{
    private const string PlacementXKey = "UI.MainWindow.X";
    private const string PlacementYKey = "UI.MainWindow.Y";
    private const string PlacementWidthKey = "UI.MainWindow.Width";
    private const string PlacementHeightKey = "UI.MainWindow.Height";
    private const string WorkspacePanelWidthKey = "UI.MainWindow.WorkspacePanelWidth";
    private const double DefaultWorkspacePanelWidth = 230;

    private ISubscriber? _subscriber;
    private bool _forceClose;
    private bool _workspaceOpen = true;
    private double _lastWorkspacePanelWidth = DefaultWorkspacePanelWidth;

#if DEBUG
    private bool _debugSystemNoticeShown;
#endif

    // 阶段 5B Phase 3：当前展示中的 Workflow 建议数据
    private string? _pendingSuggestionInput;
    private string? _pendingSuggestionSubMode;

    private readonly IAiChatEngine chatEngine = App.Services.GetRequiredService<IAiChatEngine>();

    // ──── C2：主窗口 ViewModel + DraftService ────

    private readonly Netor.Cortana.UI.ViewModels.Shared.MainWindowVm _mainVm =
        App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Shared.MainWindowVm>();

    private readonly Netor.Cortana.UI.Services.ChatDraftService _draftService =
        App.Services.GetRequiredService<Netor.Cortana.UI.Services.ChatDraftService>();


    public MainWindow()
    {
        InitializeComponent();
        Title = App.AppName;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowLoaded(object? sender, System.EventArgs e)
    {
        SubscribeEvents();
        LoadInitialData();

        // 左侧面板初始化
        LeftPanelHost.DataContext = App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Shared.LeftPanelVm>();
        LeftPanelHost.WorkspaceDirectory = App.WorkspaceDirectory;
        LeftPanelHost.AttachmentRequested += OnWorkspaceAttachmentRequested;
        LeftPanelHost.WorkspacePanelCollapseRequested += OnWorkspacePanelCollapseRequested;
        // 工作流/群聊附件回调（待重构后恢复）
        // LeftPanelHost.WorkflowAttachmentRequested += ...
        // LeftPanelHost.GroupChatAttachmentRequested += ...

        LeftPanelHost.SessionSelected += OnHistoryPanelSessionSelected;
        LeftPanelHost.RequestNewSession += OnHistoryPanelRequestNewSession;
        LeftPanelHost.WorkTaskSelected += OnWorkTaskHistorySelected;
        LeftPanelHost.MeetingSelected += OnMeetingHistorySelected;
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

    // ──────── 标题栏拖动 ────────

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ──────── 工作台切换 ────────

    private void OnWorkspaceToggleClick(object? sender, RoutedEventArgs e)
    {
        _workspaceOpen = !_workspaceOpen;
        ApplyWorkspacePanelState();
    }

    private void ApplyWorkspacePanelState()
    {
        var col = WorkspaceGrid.ColumnDefinitions[0];
        WorkspacePanelBorder.IsVisible = _workspaceOpen;
        WorkspaceExpandButton.IsVisible = !_workspaceOpen;
        WorkspaceMenuItem.Header = _workspaceOpen ? "隐藏工作台" : "显示工作台";

        if (_workspaceOpen)
        {
            col.Width = new GridLength(_lastWorkspacePanelWidth);
            col.MinWidth = 180;
            WorkspaceSplitter.IsVisible = true;
        }
        else
        {
            if (col.ActualWidth > 0)
            {
                _lastWorkspacePanelWidth = col.ActualWidth;
            }

            col.MinWidth = 0;
            col.Width = new GridLength(0);
            WorkspaceSplitter.IsVisible = false;
        }
    }

    private void OnWorkspaceMenuClick(object? sender, RoutedEventArgs e)
    {
        _workspaceOpen = !_workspaceOpen;
        ApplyWorkspacePanelState();
    }

    private void OnWorkspacePanelCollapseRequested()
    {
        if (!_workspaceOpen) return;

        _workspaceOpen = false;
        ApplyWorkspacePanelState();
    }

    private async void OnWorkspaceDirectoryMenuClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false
        });

        if (result.Count == 0) return;

        var newPath = result[0].Path.LocalPath;
        var publisher = App.Services.GetRequiredService<IPublisher>();
        publisher.Publish(Events.OnWorkspaceChanged, new WorkspaceChangedArgs(newPath));
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

            // 构建显示文本：正文已含图片 Markdown；非图片附件由资源卡片显示。
            // 图片附件已在 AiChatHostedService 中以 ![name](path) 格式注入 Content，
            // MarkdownRenderer 可直接渲染为内联预览，无需再追加 📎 标记。
            var displayText = args.Content;

            Dispatcher.UIThread.Post(() =>
            {
                ChatTabContent.HideWelcome();
                IReadOnlyList<ChatMessageAssetEntity>? assets = null;
                try
                {
                    var assetService = App.Services.GetRequiredService<ChatMessageAssetService>();
                    assets = assetService.GetByMessageId(args.UserMessageId);
                }
                catch
                {
                    assets = null;
                }

                ChatTabContent.AddMessageBubble(displayText, isUser: true, assets);
            });
            return Task.FromResult(false);
        });

        // AI 推理完成 → 刷新标题与 Token 统计。
        // 专家模式输入区的运行态已切到 ConversationTurn 事件驱动，这里不再参与 IsRunning 切换。
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiCompleted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                RefreshCurrentSessionTitle();
                RefreshTokenProgress();
            });
            return Task.FromResult(false);
        });

        // AI 配置变更 → 同步刷新三个输入模式的选择器数据。
        _subscriber.Subscribe<DataChangeArgs>(Events.OnAiProviderChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(RefreshAiConfigSelectors);
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<DataChangeArgs>(Events.OnAiModelChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(RefreshAiConfigSelectors);
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<DataChangeArgs>(Events.OnAgentChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(RefreshAiConfigSelectors);
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

    private static void RefreshAiConfigSelectors()
    {
        var chatInputVm = App.Services.GetRequiredService<ViewModels.ExpertMode.ChatInputVm>();
        chatInputVm.LoadAvailableAgents();
        chatInputVm.LoadAvailableProviders();
        if (chatInputVm.SelectedProvider is not null)
        {
            chatInputVm.LoadModelsForProvider(chatInputVm.SelectedProvider.Id);
        }

        var workInputVm = App.Services.GetRequiredService<ViewModels.WorkModeVm.WorkModeInputVm>();
        workInputVm.LoadAvailableAgents();
        workInputVm.LoadAvailableProviders();
        if (workInputVm.SelectedProvider is not null)
        {
            workInputVm.LoadModelsForProvider(workInputVm.SelectedProvider.Id);
        }

        var meetingInputVm = App.Services.GetRequiredService<ViewModels.Meeting.MeetingInputVm>();
        meetingInputVm.LoadAvailableAgents();
        meetingInputVm.LoadAvailableProviders();
        if (meetingInputVm.SelectedProvider is not null)
        {
            meetingInputVm.LoadModelsForProvider(meetingInputVm.SelectedProvider.Id);
        }
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

    private void OnWorkTaskHistorySelected(string taskId, string title)
    {
        ApplyModeToUI(WorkMode.Workflow);
        _mainVm.CurrentMode = WorkMode.Workflow;
        WorkflowTabContent.LoadTaskHistory(taskId);
    }

    public void SwitchToWorkMode(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        Dispatcher.UIThread.Post(() =>
        {
            ApplyModeToUI(WorkMode.Workflow);
            _mainVm.CurrentMode = WorkMode.Workflow;
            WorkflowTabContent.LoadTaskHistory(taskId);
        });
    }

    private async void OnMeetingHistorySelected(string meetingId, string title)
    {
        ApplyModeToUI(WorkMode.GroupChat);
        _mainVm.CurrentMode = WorkMode.GroupChat;
        await MeetingTabContent.LoadMeetingHistoryAsync(meetingId);
    }

    // ──────── ChatView WorkflowSuggestion 事件 ────────

    private void OnChatViewWorkflowSuggestionAccepted(string? pendingInput, string? subMode)
    {
        // 工作流模式待重构，暂时只切换 Tab
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;
        ApplyModeToUI(WorkMode.Workflow);
        _mainVm.CurrentMode = WorkMode.Workflow;
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

    private async void OnStoreMenuClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storeWindow = App.Services.GetRequiredService<StoreWindow>();
            if (storeWindow.IsVisible)
            {
                storeWindow.Activate();
                return;
            }

            storeWindow.Show(this);
        }
        catch (Exception ex)
        {
            await ShowStoreOpenErrorAsync(ex);
        }
    }

    private void OnProxySettingsMenuClick(object? sender, RoutedEventArgs e)
    {
        var proxy = App.Services.GetRequiredService<ProxyWindow>();
        proxy.ShowProxyWindow();
    }

    private void OnLogDirectoryMenuClick(object? sender, RoutedEventArgs e)
    {
        var path = Environment.GetEnvironmentVariable("CORTANA_LOG_DIR")
            ?? Path.Combine(App.WorkspaceDirectory, "logs");
        OpenDirectory(path);
    }

    private void OnPluginDirectoryMenuClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(App.UserPluginsDirectory);
    }

    private void OnGlobalSkillsDirectoryMenuClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(App.UserSkillsDirectory);
    }

    private void OnWorkspaceSkillsDirectoryMenuClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(App.WorkspaceSkillsDirectory);
    }

    private async void OnExitMenuClick(object? sender, RoutedEventArgs e)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        await App.ShutdownFromUiAsync(desktop);
    }

    private void OnAboutMenuClick(object? sender, RoutedEventArgs e)
    {
        var about = App.Services.GetRequiredService<AboutWindow>();
        about.ShowDialog(this);
    }

    private async Task ShowStoreOpenErrorAsync(Exception ex)
    {
        var dialog = new Window
        {
            Title = "应用商店打开失败",
            Width = 460,
            Height = 220,
            MinWidth = 460,
            MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background
        };

        var closeButton = new Button
        {
            Content = "关闭",
            MinWidth = 80,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = $"应用商店窗口初始化失败：{ex.Message}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = Foreground
                },
                closeButton
            }
        };
        Grid.SetRow(closeButton, 1);

        await dialog.ShowDialog(this);
    }

    private static void OpenDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        Process.Start(new ProcessStartInfo(directoryPath) { UseShellExecute = true });
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_forceClose && !App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.N when e.KeyModifiers == KeyModifiers.Control:
                OnNewSessionClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.B when e.KeyModifiers == KeyModifiers.Control:
                OnWorkspaceMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.O when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                OnWorkspaceDirectoryMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Back when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                OnClearMessagesClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.OemComma when e.KeyModifiers == KeyModifiers.Control:
                OnSettingsClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.P when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt):
                OnProxySettingsMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.L when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt):
                OnLogDirectoryMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt):
                OnPluginDirectoryMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Q when e.KeyModifiers == KeyModifiers.Control:
                OnExitMenuClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    internal void ApplySavedPlacement()
    {
        var settings = App.Services.GetRequiredService<SystemSettingsService>();
        ApplySavedWorkspacePanelWidth(settings);

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
        var settings = App.Services.GetRequiredService<SystemSettingsService>();
        SaveWorkspacePanelWidth(settings);

        if (WindowState != WindowState.Normal) return;
        settings.SetValue(PlacementXKey, Position.X.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementYKey, Position.Y.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementWidthKey, Width.ToString(CultureInfo.InvariantCulture));
        settings.SetValue(PlacementHeightKey, Height.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplySavedWorkspacePanelWidth(SystemSettingsService settings)
    {
        var savedWidth = ReadDouble(settings, WorkspacePanelWidthKey);
        if (savedWidth is null)
        {
            return;
        }

        _lastWorkspacePanelWidth = NormalizeWorkspacePanelWidth(savedWidth.Value);
        if (_workspaceOpen)
        {
            WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_lastWorkspacePanelWidth);
        }
    }

    private void SaveWorkspacePanelWidth(SystemSettingsService settings)
    {
        var col = WorkspaceGrid.ColumnDefinitions[0];
        var width = _workspaceOpen && col.ActualWidth > 0
            ? col.ActualWidth
            : _lastWorkspacePanelWidth;
        _lastWorkspacePanelWidth = NormalizeWorkspacePanelWidth(width);
        settings.SetValue(WorkspacePanelWidthKey, _lastWorkspacePanelWidth.ToString(CultureInfo.InvariantCulture));
    }

    private static double NormalizeWorkspacePanelWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return DefaultWorkspacePanelWidth;
        }

        return Math.Clamp(width, 180, 600);
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
        MeetingTabContent.IsVisible = toGroupChat;
        MeetingTabContent.SetVisibility(toGroupChat);

        ChatTabButton.Classes.Set("tab-btn-active", toChat);
        WorkflowTabButton.Classes.Set("tab-btn-active", toWorkflow);
        GroupChatTabButton.Classes.Set("tab-btn-active", toGroupChat);

        ToolTip.SetTip(NewItemButton, mode switch
        {
            WorkMode.Chat => "新建会话",
            WorkMode.Workflow => "新建工作",
            WorkMode.GroupChat => "新建会议",
            _ => "新建",
        });

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
            var chatInputVm = App.Services.GetRequiredService<ViewModels.ExpertMode.ChatInputVm>();
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

        // 工作流/会议模式的视图实例保持常驻，切换时只更新可见性。
    }

    // ──────── 代理方法（供 UiChatOutputChannel / App.axaml.cs 调用） ────────

    /// <summary>代理 → ChatTabContent.AddMessageBubble（UiChatOutputChannel 调用）。</summary>
    internal void AddMessageBubble(string content, bool isUser,
        IReadOnlyList<ChatMessageAssetEntity>? assets = null,
        string? authorName = null, DateTimeOffset? timestamp = null)
        => ChatTabContent.AddMessageBubble(content, isUser, assets, authorName, timestamp);

    /// <summary>代理 → ChatTabContent.CreateStreamingAssistantBubble（UiChatOutputChannel 调用）。</summary>
    internal MarkdownRenderer CreateStreamingAssistantBubble()
        => ChatTabContent.CreateStreamingAssistantBubble();

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

    /// <summary>代理 → ChatTabContent.AddToolProcessGroupCard（UiChatOutputChannel 调用）。</summary>
    internal ToolProcessGroupCard AddToolProcessGroupCard(string groupId, ChatContentSide side)
        => ChatTabContent.AddToolProcessGroupCard(groupId, side);

    /// <summary>
    /// 判断 MIME 类型是否为图片。
    /// 图片附件已在 Content 中以 Markdown 图片语法注入，MarkdownRenderer 可直接渲染预览。
    /// </summary>
    private static bool IsImageMime(string? mimeType)
        => mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
