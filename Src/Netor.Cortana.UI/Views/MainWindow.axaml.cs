using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Models;
using Netor.Cortana.UI.Views.Workspace.Controls;
using Netor.EventHub;

using System.Globalization;

namespace Netor.Cortana.UI.Views;

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
    private const string PlacementXKey = "UI.MainWindow.X";
    private const string PlacementYKey = "UI.MainWindow.Y";
    private const string PlacementWidthKey = "UI.MainWindow.Width";
    private const string PlacementHeightKey = "UI.MainWindow.Height";

    private ISubscriber? _subscriber;
    private bool _forceClose;
    private bool _workspaceOpen;
    private bool _historyPanelOpen;
#if DEBUG
    private bool _debugSystemNoticeShown;
#endif

    // 阶段 5B Phase 3：当前展示中的 Workflow 建议数据（用于"切到工作模式"按钮预填 NewTaskDialog）
    // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3。
    private string? _pendingSuggestionInput;
    private string? _pendingSuggestionSubMode;

    private readonly IAiChatEngine chatEngine = App.Services.GetRequiredService<IAiChatEngine>();

    // ──── 界面重设计 C2：MVVM 第一步 + 切换守卫 ────
    // 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §2.4。

    /// <summary>
    /// 主窗口 ViewModel（C2 引入）：承载 CurrentMode 状态 + SystemSettings 持久化（决策 DT-3）。
    /// 本期仅承载模式状态，InputBox / HistoryLabel 等内容控件仍为 code-behind 操作，
    /// C5 收尾时再考虑 Chat 全面 MVVM 化。
    /// </summary>
    private readonly Netor.Cortana.UI.ViewModels.MainWindowVm _mainVm =
        App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.MainWindowVm>();

    /// <summary>
    /// 对话草稿暂存服务（C2 引入，决策 UI-7 D2 "保留内容" 分支用）。
    /// 内存级单例，进程退出即丢失（不入数据库）。
    /// </summary>
    private readonly Netor.Cortana.UI.Services.ChatDraftService _draftService =
        App.Services.GetRequiredService<Netor.Cortana.UI.Services.ChatDraftService>();

    /// <summary>
    /// 工作台 VM（C4 引入，决策 DT-11 / DT-13）：DI Singleton，
    /// 与 TaskListPanel（左 Tab2）+ WorkflowDetailView（主区）共享同一实例。
    /// MainWindow 持有引用是为了 tab 切换时调 OnAttachedAsync 触发列表刷新
    /// （C4 之前是调 WorkflowTabContent.OnAttachedAsync，C4 拆分后 WorkflowDetailView
    /// 不再暴露此方法，改由 MainWindow 直接调 VM）。
    /// </summary>
    private readonly Netor.Cortana.UI.ViewModels.Workspace.WorkspaceTabVm _workspaceTabVm =
        App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.Workspace.WorkspaceTabVm>();

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

        // 界面重设计 C3：左侧面板初始化（替代原 WorkspacePanel，引入底部 Tab 切换 + L2 联动）。
        // DataContext = LeftPanelVm（DI Singleton，与 MainWindowVm 共享 PropertyChanged 联动）。
        // 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §3。
        LeftPanelHost.DataContext = App.Services.GetRequiredService<Netor.Cortana.UI.ViewModels.LeftPanelVm>();
        LeftPanelHost.WorkspaceDirectory = App.WorkspaceDirectory;
        LeftPanelHost.AttachmentRequested += OnWorkspaceAttachmentRequested;

        // 历史记录面板初始化
        HistoryPanel.SessionSelected += OnHistoryPanelSessionSelected;
        HistoryPanel.RequestNewSession += OnHistoryPanelRequestNewSession;
        HistoryPanel.AttachScrollHandler();

        // Token 使用量变更 → 实时刷新进度条（跨 ChatClient 重建保留数值）
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        factory.TokenUsageChanged += RefreshTokenProgress;
        RefreshTokenProgress();

        // 界面重设计 C2：启动时按 _mainVm.CurrentMode 恢复 UI 状态（决策 DT-3）。
        // _mainVm 构造函数已从 SystemSettings 恢复值，这里只需把 UI 同步到 VM。
        // 注意：不能调用 _mainVm.CurrentMode = ... 触发 setter，因为那会触发持久化写一遍。
        // 仅当 VM 恢复值非 Chat（默认）时才同步 UI，避免对已 active 的 ChatTab 反复重设。
        // 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §2.4 + 03-交互细节.md §1.1。
        if (_mainVm.CurrentMode != Netor.Cortana.UI.Models.WorkMode.Chat)
        {
            ApplyModeToUI(_mainVm.CurrentMode);

            // 工作流模式：触发 WorkflowTab 数据加载（C4：改为直接调 DI Singleton VM）
            if (_mainVm.CurrentMode == Netor.Cortana.UI.Models.WorkMode.Workflow)
            {
                _ = _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty);
            }
        }

        ShowDebugSystemNotice();
    }

    private void ShowDebugSystemNotice()
    {
#if DEBUG
        if (_debugSystemNoticeShown)
        {
            return;
        }

        _debugSystemNoticeShown = true;
        HideWelcome();
        AddSystemNotice(new SystemNoticeArgs(
            "这是调试模式下用于验收 system.notice 样式的系统提醒。\n当前默认只预览两行内容。\n点击标题左侧图标可以展开完整详情。\n再次点击可以折叠回预览状态。",
            "系统提醒样式验收",
            "info",
            "调试模式",
            DateTimeOffset.Now));
#endif
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

        _subscriber.Subscribe<SystemNoticeArgs>(Events.OnSystemNotice, (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Content)) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                HideWelcome();
                AddSystemNotice(args);
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

        // 阶段 5B Phase 3：订阅 Chat→Workflow 启发式建议事件，UI 端弹 banner
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3
        _subscriber.Subscribe<WorkflowSuggestionArgs>(Events.OnWorkflowSuggestion, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _pendingSuggestionInput = args.OriginalInput;
                _pendingSuggestionSubMode = args.SuggestedSubMode;

                WorkflowSuggestionReason.Text = args.Reason;
                // input preview 截断到 80 字符方便单行展示
                var preview = args.OriginalInput.Length > 80
                    ? args.OriginalInput[..80] + "…"
                    : args.OriginalInput;
                WorkflowSuggestionInputPreview.Text = preview;

                WorkflowSuggestionBanner.IsVisible = true;
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
        if (!_forceClose && !App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>从系统设置恢复主窗口位置和大小；首次启动无设置时保持 XAML 的居中启动。</summary>
    internal void ApplySavedPlacement()
    {
        var settings = App.Services.GetRequiredService<SystemSettingsService>();
        var x = ReadInt(settings, PlacementXKey);
        var y = ReadInt(settings, PlacementYKey);
        var width = ReadDouble(settings, PlacementWidthKey);
        var height = ReadDouble(settings, PlacementHeightKey);

        if (x is null || y is null || width is null || height is null)
        {
            return;
        }

        Width = Math.Max(MinWidth, width.Value);
        Height = Math.Max(MinHeight, height.Value);

        var position = new PixelPoint(x.Value, y.Value);
        if (IsPositionVisible(position))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }
    }

    /// <summary>保存当前主窗口位置和大小到系统设置表。</summary>
    internal void SaveCurrentPlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

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
            {
                return true;
            }
        }

        return false;
    }

    private static int? ReadInt(SystemSettingsService settings, string key)
    {
        return int.TryParse(settings.GetValue(key), CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ReadDouble(SystemSettingsService settings, string key)
    {
        return double.TryParse(settings.GetValue(key), CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
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

    // ────────────────────────────────────────────────────────────
    // 阶段 3B：Chat / Workspace Tab 切换
    // 界面重设计 C2：扩展为 3 tab（对话 / 工作流 / 群聊）+ 未保存确认守卫（决策 UI-3 + UI-7）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 当前激活的 tab 字符串（保留供 5B 阶段已有逻辑兼容；C2 起以 _mainVm.CurrentMode 为权威源）。
    /// </summary>
    private string _currentTab = "chat";

    /// <summary>
    /// 把 Models.WorkMode 映射到 axaml 控件可见性 + tab 按钮 active 样式 + _currentTab。
    /// 注意：本方法只改 UI，不改 VM。VM 由调用方在合适时机设置以触发持久化（决策 DT-3）。
    /// </summary>
    private void ApplyModeToUI(Netor.Cortana.UI.Models.WorkMode mode)
    {
        var toChat = mode == Netor.Cortana.UI.Models.WorkMode.Chat;
        var toWorkflow = mode == Netor.Cortana.UI.Models.WorkMode.Workflow;
        var toGroupChat = mode == Netor.Cortana.UI.Models.WorkMode.GroupChat;

        ChatTabContent.IsVisible = toChat;
        WorkflowTabContent.IsVisible = toWorkflow;
        GroupChatTabContent.IsVisible = toGroupChat;

        ChatTabButton.Classes.Set("tab-btn-active", toChat);
        WorkflowTabButton.Classes.Set("tab-btn-active", toWorkflow);
        GroupChatTabButton.Classes.Set("tab-btn-active", toGroupChat);

        _currentTab = mode.ToPersistenceString();
    }

    /// <summary>
    /// 顶部 Tab 按钮点击：在 对话 / 工作流 / 群聊 之间切换主内容区。
    ///
    /// 界面重设计 C2 行为（决策 UI-7 D2）：
    /// - 从 对话 → 工作流/群聊 且输入框 / 附件有未保存内容时，弹 Dialogs.UnsavedChangesDialog
    /// - 用户选 取消：留在 对话 模式，不切换
    /// - 用户选 保留内容：切换 + ChatDraftService.Save 暂存（IsVisible 切换自然保留 InputBox 状态）
    /// - 用户选 丢弃并切换：清空 InputBox + 附件 + 切换
    ///
    /// 切换时仅修改 IsVisible 与按钮 active 样式，不卸载控件本身（决策 DT-5 A：保留 Chat Tab 状态）。
    /// </summary>
    private async void OnTabSwitchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        if (_currentTab == tab) return;

        var targetMode = Netor.Cortana.UI.Models.WorkModeExtensions.FromPersistenceString(tab);

        // C2 守卫：仅当从 Chat → Workflow/GroupChat 且有未保存内容时弹确认对话框
        if (_currentTab == "chat" && targetMode != Netor.Cortana.UI.Models.WorkMode.Chat)
        {
            var hasText = !string.IsNullOrWhiteSpace(InputBox?.Text);
            var hasAttachments = _attachments.Count > 0;
            if (hasText || hasAttachments)
            {
                try
                {
                    var preview = hasText ? InputBox!.Text! : string.Empty;
                    if (preview.Length > 50) preview = string.Concat(preview.AsSpan(0, 50), "…");

                    var choice = await Netor.Cortana.UI.Views.Dialogs.UnsavedChangesDialog
                        .ShowDialogAsync(this, preview, _attachments.Count);

                    switch (choice)
                    {
                        case Netor.Cortana.UI.Views.Dialogs.UnsavedChoice.Cancel:
                            return; // 用户取消 → 不切

                        case Netor.Cortana.UI.Views.Dialogs.UnsavedChoice.Save:
                            // 切走，通过 DraftService 暂存（IsVisible 切换自然保留 InputBox 状态；
                            // Save 调用为 C5+ Chat 全面 MVVM 化后真正销毁/重建 ChatTab 时的铺路）。
                            _draftService.Save(InputBox?.Text, _attachments);
                            break;

                        case Netor.Cortana.UI.Views.Dialogs.UnsavedChoice.Discard:
                            // 切走，清空 InputBox + 附件
                            if (InputBox is not null) InputBox.Text = string.Empty;
                            _attachments.Clear();
                            // 附件 UI 重绘由 AttachmentList ItemsSource 重新设置触发；
                            // C2 阶段简化处理：用户切到工作流后再切回对话时附件 UI 可能仍残留旧 chip，
                            // 这是已知小问题，留待 C5 收尾时附 attachment ItemsSource 绑定方案统一修复。
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainWindow] UnsavedChangesDialog 异常：{ex.Message}");
                    return; // 异常 fallback：不切（保守，避免静默丢失数据）
                }
            }
        }

        // 执行切换
        ApplyModeToUI(targetMode);
        _mainVm.CurrentMode = targetMode; // 触发 PropertyChanged + 持久化到 SystemSettings（决策 DT-3）

        if (targetMode != Netor.Cortana.UI.Models.WorkMode.Chat)
        {
            // 切到非对话模式：关闭 Chat Tab 的历史下拉（避免主内容被遮挡）
            if (HistoryPopup?.IsOpen == true) HistoryPopup.IsOpen = false;
        }

        if (targetMode == Netor.Cortana.UI.Models.WorkMode.Workflow)
        {
            // 通知 WorkflowTab 拉取最新列表（C4：改为直接调 DI Singleton VM；workspaceId 简化版传空字符串）
            try
            {
                await _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] WorkflowTab attach error: {ex.Message}");
            }
        }
        // GroupChat 模式：C2 阶段仅显示 EmptyState 占位，无 OnAttached 逻辑（C4 拆分时补全）。
    }

    // ──────── 阶段 5B Phase 3：Chat→Workflow 启发式建议 banner ────────

    /// <summary>
    /// 用户点击 [切到工作模式]：跳转到工作台 Tab + 弹 NewTaskDialog 预填 InitialInput / SubMode。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3。
    /// </summary>
    private async void OnWorkflowSuggestionAcceptClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var input = _pendingSuggestionInput ?? string.Empty;
        var subMode = _pendingSuggestionSubMode ?? "Magentic";

        // 1) 隐藏 banner（无论后续步骤是否成功）
        WorkflowSuggestionBanner.IsVisible = false;
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;

        try
        {
            // 2) 切到工作流 Tab（界面重设计 C2：复用 ApplyModeToUI + VM 设值，与 OnTabSwitchClick 一致）
            //    注意：本路径无需弹未保存确认对话框，因为入口是用户主动点 Banner "切到工作模式"，
            //          已隐含了"我要切走"的意图，且 input 已被 _pendingSuggestionInput 持有，
            //          后续会预填到 NewTaskDialog（不依赖 InputBox 文本）。
            if (_currentTab != "workflow")
            {
                ApplyModeToUI(Netor.Cortana.UI.Models.WorkMode.Workflow);
                _mainVm.CurrentMode = Netor.Cortana.UI.Models.WorkMode.Workflow;

                if (HistoryPopup?.IsOpen == true) HistoryPopup.IsOpen = false;

                try
                {
                    await _workspaceTabVm.OnAttachedAsync(workspaceId: string.Empty);
                }
                catch (Exception innerEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainWindow] WorkflowTab attach error: {innerEx.Message}");
                }
            }

            // 3) 弹 NewTaskDialog 并预填 InitialInput / SubMode
            var dialog = new NewTaskDialog
            {
                WorkspaceId = string.Empty,
                InitialInput = input,
                SubMode = subMode,
            };
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainWindow] OnWorkflowSuggestionAcceptClick error: {ex.Message}");
        }
    }

    /// <summary>
    /// 用户点击 [✕] 忽略本次建议：仅隐藏 banner，不影响下次触发判断。
    /// </summary>
    private void OnWorkflowSuggestionDismissClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WorkflowSuggestionBanner.IsVisible = false;
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;
    }
}