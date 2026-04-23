using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// 主对话窗口 —— 会话列表 + Markdown 消息渲染 + 文本输入。
/// 职责拆分：
/// <list type="bullet">
/// <item><c>MainWindow.Sessions.cs</c>：会话历史加载 / 切换 / 欢迎面板。</item>
/// <item><c>MainWindow.Selectors.cs</c>：智能体 / 厂商 / 模型 三级选择器。</item>
/// <item><c>MainWindow.Attachments.cs</c>：附件管理 + 拖放文件。</item>
/// <item><c>MainWindow.Input.cs</c>：输入框键盘快捷键 + #文件 / @智能体 自动补全。</item>
/// <item><c>MainWindow.Messaging.cs</c>：消息发送 / 取消 / 气泡渲染 / 滚动。</item>
/// </list>
/// 本文件仅保留窗口生命周期、事件订阅与面板切换。
/// </summary>
public partial class MainWindow : Window
{
    private ISubscriber? _subscriber;
    private bool _forceClose;
    private bool _workspaceOpen;
    private bool _historyPanelOpen;

    private readonly IAiChatEngine chatEngine = App.Services.GetRequiredService<IAiChatEngine>();

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
        MessageScroller.ScrollChanged += OnScrollChanged;
        InputBox.TextChanged += OnInputTextChanged;

        // 拖放文件支持
        InputBorder.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        InputBorder.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        InputBorder.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        InputBorder.AddHandler(DragDrop.DropEvent, OnDrop);

        // 输入框焦点激活效果
        InputBox.GotFocus += OnInputBoxGotFocus;
        InputBox.LostFocus += OnInputBoxLostFocus;

        // 隧道阶段拦截 Enter 发送，优先于 TextBox 自身的 AcceptsReturn 处理
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // 工作台面板初始化
        WorkspacePanel.WorkspaceDirectory = App.WorkspaceDirectory;
        WorkspacePanel.AttachmentRequested += OnWorkspaceAttachmentRequested;

        // 历史记录面板初始化
        HistoryPanel.SessionSelected += OnHistoryPanelSessionSelected;
        HistoryPanel.RequestNewSession += OnHistoryPanelRequestNewSession;
        HistoryPanel.AttachScrollHandler();

        // Token 使用量变更 → 实时刷新进度条（跨 ChatClient 重建保留数值）
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        factory.TokenUsageChanged += RefreshTokenProgress;
        RefreshTokenProgress();
    }

    /// <summary>加载初始数据（会话历史、智能体、厂商、模型选择器）。</summary>
    private void LoadInitialData()
    {
        LoadSessions();
        LoadAgents();
        LoadProviders();
    }

    // ──────── 工作台 / 历史面板切换 ────────

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

    private void OnHistoryPanelToggleClick(object? sender, RoutedEventArgs e)
    {
        _historyPanelOpen = !_historyPanelOpen;
        var col = WorkspaceGrid.ColumnDefinitions[4];
        if (_historyPanelOpen)
        {
            col.Width = new GridLength(280);
            col.MinWidth = 180;
            HistorySplitter.IsVisible = true;
            HistoryPanel.Reload();
        }
        else
        {
            col.MinWidth = 0;
            col.Width = new GridLength(0);
            HistorySplitter.IsVisible = false;
        }
    }

    // ──────── 事件订阅（EventHub） ────────

    /// <summary>
    /// 订阅 EventHub 事件，接收 AI 回复和配置变更。
    /// </summary>
    private void SubscribeEvents()
    {
        _subscriber = App.Services.GetRequiredService<ISubscriber>();

        // AI 配置变更 → 刷新选择器
        _subscriber.Subscribe<DataChangeArgs>(Events.OnAiProviderChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(LoadProviders);
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<DataChangeArgs>(Events.OnAiModelChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(_currentProviderId))
                    LoadModels(_currentProviderId);
            });
            return Task.FromResult(false);
        });

        _subscriber.On(Events.OnAgentChange, (_, _) =>
        {
            Dispatcher.UIThread.Post(LoadAgents);
            return Task.FromResult(false);
        });

        // AI 推理开始 → 切换按钮为取消状态
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiStarted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() => SetSendingState(true));
            return Task.FromResult(false);
        });

        // AI 推理完成 → 恢复按钮为发送状态 + 刷新侧边栏标题
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiCompleted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetSendingState(false);
                RefreshCurrentSessionTitle();
            });
            return Task.FromResult(false);
        });

        // AI 生成会话标题完成 → 刷新标题
        _subscriber.Subscribe<SessionTitleUpdatedArgs>(Events.OnSessionTitleUpdated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(HistoryPanel.CurrentSessionId, args.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    HistoryLabel.Text = args.Title;
                }
                RefreshCurrentSessionTitle();
            });
            return Task.FromResult(false);
        });

        // 语音识别最终结果 → 显示用户消息气泡
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Text)) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                HideWelcome();
                AddMessageBubble(args.Text, isUser: true);
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<WebSocketUserMessageReceivedArgs>(Events.OnWebSocketUserMessageReceived, (_, args) =>
        {
            var attachmentNames = args.Attachments.Count > 0
                ? string.Join(", ", args.Attachments.Select(attachment => $"📎 {attachment.Name}"))
                : string.Empty;
            var displayText = string.IsNullOrWhiteSpace(attachmentNames)
                ? args.Text
                : string.IsNullOrWhiteSpace(args.Text)
                    ? attachmentNames
                    : $"{args.Text}\n{attachmentNames}";

            if (string.IsNullOrWhiteSpace(displayText))
            {
                return Task.FromResult(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                HideWelcome();
                AddMessageBubble(displayText, isUser: true);
            });

            return Task.FromResult(false);
        });

        // 工作目录变更 → 刷新文件树 + 重载会话
        _subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                WorkspacePanel.WorkspaceDirectory = args.Path;
                LoadSessions();

                // 刷新历史记录面板
                if (_historyPanelOpen)
                    HistoryPanel.Reload();

                // 新工作目录没有任何会话时，主动创建一个
                if (HistoryList.Items.Count == 0)
                {
                    Task.Run(() => chatEngine.NewSessionAsync());
                }
            });
            return Task.FromResult(false);
        });

        // 新会话已创建 → 刷新列表 + 切换到新会话
        _subscriber.Subscribe<SessionCreatedArgs>(Events.OnSessionCreated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MessageList.Items.Clear();
                HistoryLabel.Text = "新对话";
                ShowWelcome();
                LoadSessions();
                // 关键：切换到新创建的会话，而不是继续在旧会话上
                SwitchToSession(args.SessionId, "新对话");
            });
            return Task.FromResult(false);
        });
    }

    // ──────── 设置 / 关闭 ────────

    private void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsWindow>();
        settings.Show();
        settings.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 用户点关闭按钮时隐藏窗口，不退出程序
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>强制关闭窗口（退出应用时使用）。</summary>
    internal void ForceClose()
    {
        _forceClose = true;
        _subscriber?.Dispose();
        try
        {
            var factory = App.Services.GetRequiredService<AIAgentFactory>();
            factory.TokenUsageChanged -= RefreshTokenProgress;
        }
        catch { /* 关闭路径安静处理 */ }
        Close();
    }
}