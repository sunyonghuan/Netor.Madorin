using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

using Netor.Cortana.AvaloniaUI.Controls;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// 主对话窗口——会话列表 + Markdown 消息渲染 + 文本输入。
/// 复用现有 WebSocket 通信层（IChatTransport），不再需要 COM BridgeHostObject。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Bitmap AiAvatarBitmap = LoadAiAvatarBitmap();

    private ISubscriber? _subscriber;
    private bool _forceClose;
    private bool _workspaceOpen;
    private bool _historyPanelOpen;

    // 当前选中的厂商 ID（用于厂商→模型联动）
    private string _currentProviderId = string.Empty;

    // 缓存数据列表（用于选择器项点击回调）
    private List<AgentEntity> _agents = [];

    private List<AiProviderEntity> _providers = [];
    private List<AiModelEntity> _models = [];
    private IAiChatEngine chatEngine = App.Services.GetRequiredService<IAiChatEngine>();

    // 待发送的附件列表
    private readonly List<AttachmentInfo> _attachments = [];

    // @智能体提及：名称 → AgentEntity 映射
    private readonly Dictionary<string, AgentEntity> _agentMentions = new(StringComparer.OrdinalIgnoreCase);
    private List<AgentEntity> _currentAgentSuggestions = [];
    private int _agentPopupSelectedIndex = -1;
    private int _currentAgentAtIndex = -1;

    // AI 对话进行中标志 & 取消令牌
    private bool _isSending;

    private CancellationTokenSource? _sendCts;
    private Animation? _spinnerAnimation;

    // 用户是否手动向上滚动（此时不自动跟随）
    private bool _userScrolledUp;

    // # 文件补全：文件名 → 完整路径 映射
    private readonly Dictionary<string, string> _fileReferences = new(StringComparer.OrdinalIgnoreCase);

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
    }

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

    private void OnHistoryPanelSessionSelected(string sessionId, string title)
    {
        SwitchToSession(sessionId, title);
    }

    private void OnHistoryPanelRequestNewSession()
    {
        Task.Run(() => chatEngine.NewSessionAsync());
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = MessageScroller;
        // 距底部不超过 30px 时视为"在底部"
        _userScrolledUp = sv.Offset.Y + sv.Viewport.Height < sv.Extent.Height - 30;
        ScrollToBottomBtn.IsVisible = _userScrolledUp;
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        ForceScrollToBottom();
    }

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

    /// <summary>
    /// 加载初始数据（会话历史、智能体、厂商、模型选择器）。
    /// </summary>
    private void LoadInitialData()
    {
        LoadSessions();
        LoadAgents();
        LoadProviders();
    }

    // ──────── 会话历史管理 ────────

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
                AddMessageBubble(msg.Content, isUser, msgAssets);
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

    // ──────── 选择器：智能体 ────────

    /// <summary>
    /// 加载智能体列表，填充选择器并设置标题栏和工具栏标签。
    /// </summary>
    private void LoadAgents()
    {
        try
        {
            var agentService = App.Services.GetRequiredService<AgentService>();
            _agents = agentService.GetAll();

            var defaultAgent = _agents.FirstOrDefault(a => a.IsDefault) ?? _agents.FirstOrDefault();
            RefreshAgentDisplay(defaultAgent);
            FillAgentSelector(defaultAgent?.Id);
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载智能体列表失败");
        }
    }

    private void RefreshAgentDisplay(AgentEntity? agent = null)
    {
        if (agent is null)
        {
            var agentService = App.Services.GetRequiredService<AgentService>();
            agent = agentService.GetAll().FirstOrDefault(a => a.IsDefault);
        }

        var name = agent?.Name ?? "默认助手";
        //AgentLabel.Text = name;
        ToolbarAgentLabel.Text = name;
    }

    private void FillAgentSelector(string? activeId)
    {
        AgentSelectorList.Items.Clear();
        foreach (var agent in _agents)
        {
            var btn = new Button
            {
                Classes = { agent.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = agent.Id,
                Content = new TextBlock
                {
                    Text = agent.Name,
                    FontSize = 12,
                    Foreground = agent.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnAgentItemClick;
            AgentSelectorList.Items.Add(btn);
        }
    }

    // ──────── 选择器：厂商 ────────

    /// <summary>
    /// 加载厂商列表，填充选择器并触发模型联动加载。
    /// </summary>
    private void LoadProviders()
    {
        try
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            _providers = providerService.GetAll();

            var defaultProvider = _providers.FirstOrDefault(p => p.IsDefault) ?? _providers.FirstOrDefault();
            _currentProviderId = defaultProvider?.Id ?? string.Empty;

            RefreshProviderDisplay(defaultProvider);
            FillProviderSelector(defaultProvider?.Id);

            // 联动加载该厂商的模型
            if (!string.IsNullOrEmpty(_currentProviderId))
            {
                LoadModels(_currentProviderId);
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载厂商列表失败");
        }
    }

    private void RefreshProviderDisplay(AiProviderEntity? provider = null)
    {
        if (provider is null)
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            provider = providerService.GetAll().FirstOrDefault(p => p.IsDefault);
        }

        ToolbarProviderLabel.Text = provider?.Name ?? "厂商";
    }

    private void FillProviderSelector(string? activeId)
    {
        ProviderList.Items.Clear();
        foreach (var provider in _providers)
        {
            var btn = new Button
            {
                Classes = { provider.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = provider.Id,
                Content = new TextBlock
                {
                    Text = provider.Name,
                    FontSize = 12,
                    Foreground = provider.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnProviderItemClick;
            ProviderList.Items.Add(btn);
        }
    }

    // ──────── 选择器：模型 ────────

    /// <summary>
    /// 根据厂商 ID 加载模型列表并填充选择器。
    /// </summary>
    private void LoadModels(string providerId)
    {
        try
        {
            var modelService = App.Services.GetRequiredService<AiModelService>();
            _models = modelService.GetByProviderId(providerId);

            // 若本地无模型，尝试从远程拉取
            if (_models.Count == 0)
            {
                var providerService = App.Services.GetRequiredService<AiProviderService>();
                var provider = providerService.GetById(providerId);
                if (provider is not null)
                {
                    var fetcher = App.Services.GetRequiredService<AiModelFetcherService>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await fetcher.FetchAndSaveModelsAsync(provider);
                            Dispatcher.UIThread.Post(() => LoadModels(providerId));
                        }
                        catch (Exception ex)
                        {
                            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
                            logger.LogWarning(ex, "远程拉取模型列表失败");
                        }
                    });
                }
                return;
            }

            var defaultModel = _models.FirstOrDefault(m => m.IsDefault) ?? _models.FirstOrDefault();
            RefreshModelDisplay(defaultModel);
            FillModelSelector(defaultModel?.Id);

            // 通知 AiChatService 当前模型
            if (defaultModel is not null)
            {
                var chatService = App.Services.GetRequiredService<AiChatHostedService>();
                chatService.ChangeModel(defaultModel.Id);
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载模型列表失败");
        }
    }

    private void RefreshModelDisplay(AiModelEntity? model = null)
    {
        if (model is null)
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            var modelService = App.Services.GetRequiredService<AiModelService>();

            foreach (var provider in providerService.GetAll())
            {
                model = modelService.GetByProviderId(provider.Id)
                    .FirstOrDefault(m => m.IsDefault);
                if (model is not null) break;
            }
        }

        var displayName = model?.DisplayName ?? "未选择模型";
        //ModelLabel.Text = displayName;
        ToolbarModelLabel.Text = displayName;
    }

    private void FillModelSelector(string? activeId)
    {
        ModelList.Items.Clear();
        foreach (var model in _models)
        {
            var btn = new Button
            {
                Classes = { model.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = model.Id,
                Content = new TextBlock
                {
                    Text = model.DisplayName,
                    FontSize = 12,
                    Foreground = model.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnModelItemClick;
            ModelList.Items.Add(btn);
        }
    }

    private void RefreshAgentDisplay()
    {
        RefreshAgentDisplay(null);
    }

    // ──────── 附件管理 ────────

    /// <summary>
    /// 打开文件选择对话框，添加附件。
    /// </summary>
    private async void OnAttachClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择附件",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("文档文件") { Patterns = ["*.pdf", "*.doc", "*.docx", "*.txt", "*.md", "*.csv", "*.xlsx", "*.pptx"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("脚本与源码") { Patterns = ["*.cs", "*.csx", "*.ps1", "*.psm1", "*.psd1", "*.py", "*.pyw", "*.cmd", "*.bat", "*.sh", "*.js", "*.ts", "*.json", "*.yml", "*.yaml", "*.xml"] },
                    ]
                });

            foreach (var file in files)
            {
                var path = file.Path.LocalPath;
                var name = file.Name;
                var mimeType = FileContentTypeResolver.GetMimeType(path);
                _attachments.Add(new AttachmentInfo(path, name, mimeType));
            }

            if (files.Count > 0)
            {
                RenderAttachments();
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "打开文件选择器失败");
        }
    }

    /// <summary>
    /// 渲染附件预览列表。
    /// </summary>
    private void RenderAttachments()
    {
        AttachmentList.Items.Clear();

        for (int i = 0; i < _attachments.Count; i++)
        {
            var attachment = _attachments[i];
            var index = i;

            var removeBtn = new Button
            {
                Content = "✕",
                Background = Avalonia.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                FontSize = 11,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                BorderThickness = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Tag = index,
            };
            removeBtn.Click += OnRemoveAttachmentClick;

            var tag = new Border
            {
                Classes = { "attachment-tag" },
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"📎 {attachment.Name}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            MaxWidth = 180,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        },
                        removeBtn
                    }
                }
            };

            AttachmentList.Items.Add(tag);
        }
    }

    /// <summary>
    /// 移除指定附件。
    /// </summary>
    private void OnRemoveAttachmentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index >= 0 && index < _attachments.Count)
        {
            _attachments.RemoveAt(index);
            RenderAttachments();
        }
    }

    /// <summary>
    /// 清空所有附件。
    /// </summary>
    private void ClearAttachments()
    {
        _attachments.Clear();
        AttachmentList.Items.Clear();
    }

    /// <summary>
    /// 工作台文件树"发送到聊天附件"回调。
    /// </summary>
    private void OnWorkspaceAttachmentRequested(IReadOnlyList<string> filePaths)
    {
        var added = false;
        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            // 避免重复添加
            if (_attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _attachments.Add(new AttachmentInfo(path, Path.GetFileName(path), FileContentTypeResolver.GetMimeType(path)));
            added = true;
        }
        if (added) RenderAttachments();
    }

    // ──────── 拖放文件 ────────

    private static readonly IBrush BorderNormal = SolidColorBrush.Parse("#3c3c3c");
    private static readonly IBrush BorderActive = SolidColorBrush.Parse("#007ACC");
    private static readonly IBrush BgNormal = SolidColorBrush.Parse("#252526");
    private static readonly IBrush BgDragOver = SolidColorBrush.Parse("#1a007ACC");
    private bool _isDragOver;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        _isDragOver = true;
        InputBorder.BorderBrush = BorderActive;
        InputBorder.BorderThickness = new Thickness(2);
        InputBorder.Background = BgDragOver;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var added = false;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

            var name = Path.GetFileName(path);
            var mimeType = FileContentTypeResolver.GetMimeType(path);
            _attachments.Add(new AttachmentInfo(path, name, mimeType));
            added = true;
        }

        if (added)
        {
            RenderAttachments();
        }

        e.Handled = true;
    }

    // ──────── 输入框焦点效果 ────────

    private void OnInputBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!_isDragOver)
        {
            InputBorder.BorderBrush = BorderActive;
        }
    }

    private void OnInputBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_isDragOver)
        {
            InputBorder.BorderBrush = BorderNormal;
        }
    }

    private void RestoreInputBorder()
    {
        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.Background = BgNormal;
        InputBorder.BorderBrush = InputBox.IsFocused ? BorderActive : BorderNormal;
    }

    // ──────── 输入处理 ────────

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // AgentPopup 键盘导航
        if (AgentPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = (_agentPopupSelectedIndex + 1) % _currentAgentSuggestions.Count;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = _agentPopupSelectedIndex <= 0
                        ? _currentAgentSuggestions.Count - 1
                        : _agentPopupSelectedIndex - 1;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_agentPopupSelectedIndex >= 0 && _agentPopupSelectedIndex < _currentAgentSuggestions.Count)
                {
                    OnAgentItemSelected(_currentAgentSuggestions[_agentPopupSelectedIndex], _currentAgentAtIndex);
                }
                else if (_currentAgentSuggestions.Count > 0)
                {
                    OnAgentItemSelected(_currentAgentSuggestions[0], _currentAgentAtIndex);
                }
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AgentPopup.IsOpen = false;
                return;
            }
        }

        // FilePopup 键盘导航
        if (FilePopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                FilePopup.IsOpen = false;
                return;
            }
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            // 隧道阶段拦截：阻止 TextBox 插入换行，改为发送消息
            e.Handled = true;
            if (FilePopup.IsOpen)
                FilePopup.IsOpen = false;
            if (AgentPopup.IsOpen)
                AgentPopup.IsOpen = false;
            SendMessage();
        }
    }

    // ──────── # 文件补全 ────────

    /// <summary>
    /// 输入框文本变化时检测 # 触发文件补全、@ 触发智能体补全。
    /// </summary>
    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 尝试 @ 智能体补全
        if (TryShowAgentPopup(text, caret))
        {
            FilePopup.IsOpen = false;
            return;
        }

        AgentPopup.IsOpen = false;

        // 尝试 # 文件补全
        var hashIndex = text.LastIndexOf('#', Math.Max(0, caret - 1));
        if (hashIndex < 0)
        {
            FilePopup.IsOpen = false;
            return;
        }

        // # 后面到光标之间的内容作为搜索关键字
        var afterHash = text.Substring(hashIndex + 1, caret - hashIndex - 1);

        // 如果包含空格，关闭补全
        if (afterHash.Contains(' ') || afterHash.Contains('\n'))
        {
            FilePopup.IsOpen = false;
            return;
        }

        // 获取工作目录下的文件列表（过滤匹配）
        var appPaths = App.Services.GetRequiredService<IAppPaths>();
        var workDir = appPaths.WorkspaceDirectory;

        if (!Directory.Exists(workDir))
        {
            FilePopup.IsOpen = false;
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                .Select(f => (FullPath: f, RelativePath: Path.GetRelativePath(workDir, f)))
                .Where(f => !f.RelativePath.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal)
                         && !f.RelativePath.StartsWith('.'))
                .Where(f => string.IsNullOrEmpty(afterHash)
                         || f.RelativePath.Contains(afterHash, StringComparison.OrdinalIgnoreCase))
                .Take(30)
                .ToList();

            if (files.Count == 0)
            {
                FilePopup.IsOpen = false;
                return;
            }

            FillFileList(files, hashIndex);
            FilePopup.IsOpen = true;
        }
        catch
        {
            FilePopup.IsOpen = false;
        }
    }

    /// <summary>
    /// 填充文件补全列表。
    /// </summary>
    private void FillFileList(List<(string FullPath, string RelativePath)> files, int hashIndex)
    {
        FileList.Items.Clear();

        foreach (var (fullPath, relativePath) in files)
        {
            var fileName = Path.GetFileName(fullPath);
            var dirPart = Path.GetDirectoryName(relativePath) ?? "";

            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = fileName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            });
            if (!string.IsNullOrEmpty(dirPart))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = dirPart,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                });
            }

            var btn = new Button
            {
                Classes = { "selector-item" },
                Content = sp,
                Tag = fullPath,
            };
            btn.Click += (_, _) => OnFileItemSelected(fileName, fullPath, hashIndex);
            FileList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 选中文件后：将 #关键字 替换为文件名，记录文件名→路径映射。
    /// </summary>
    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        FilePopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 替换 # 到光标之间的内容为 #文件名
        var replacement = $"#{fileName} ";
        var newText = string.Concat(text.AsSpan(0, hashIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = hashIndex + replacement.Length;

        // 记录映射
        _fileReferences[fileName] = fullPath;
    }

    // ──────── @ 智能体补全 ────────

    /// <summary>
    /// 检测 @ 触发智能体补全弹窗。
    /// </summary>
    private bool TryShowAgentPopup(string text, int caret)
    {
        if (caret <= 0) return false;

        var atIndex = text.LastIndexOf('@', Math.Max(0, caret - 1));
        if (atIndex < 0) return false;

        // @ 前面只能是行首或空白字符
        if (atIndex > 0 && !char.IsWhiteSpace(text[atIndex - 1])) return false;

        var afterAt = text.Substring(atIndex + 1, caret - atIndex - 1);

        if (afterAt.Contains(' ') || afterAt.Contains('\n')) return false;

        var agentService = App.Services.GetRequiredService<AgentService>();
        var allAgents = agentService.GetAll();

        var matches = string.IsNullOrEmpty(afterAt)
            ? allAgents
            : allAgents.Where(a => a.Name.Contains(afterAt, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            AgentPopup.IsOpen = false;
            return false;
        }

        _currentAgentSuggestions = matches;
        _currentAgentAtIndex = atIndex;
        _agentPopupSelectedIndex = 0;
        FillAgentList(matches, atIndex);
        HighlightAgentItem(0);
        AgentPopup.IsOpen = true;
        return true;
    }

    /// <summary>
    /// 填充智能体补全列表。
    /// </summary>
    private void FillAgentList(List<AgentEntity> agents, int atIndex)
    {
        AgentList.Items.Clear();

        foreach (var agent in agents)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = agent.Name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            });
            if (!string.IsNullOrWhiteSpace(agent.Description))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = agent.Description.Length > 60 ? agent.Description[..60] + "…" : agent.Description,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                });
            }

            var btn = new Button
            {
                Classes = { "selector-item" },
                Content = sp,
                Tag = agent,
            };
            btn.Click += (_, _) => OnAgentItemSelected(agent, atIndex);
            AgentList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 选中智能体后：将 @关键字 替换为 @智能体名 并记录提及。
    /// </summary>
    private void OnAgentItemSelected(AgentEntity agent, int atIndex)
    {
        AgentPopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        var replacement = $"@{agent.Name} ";
        var newText = string.Concat(text.AsSpan(0, atIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = atIndex + replacement.Length;

        _agentMentions[agent.Name] = agent;
    }

    /// <summary>
    /// 高亮指定索引的智能体列表项。
    /// </summary>
    private void HighlightAgentItem(int index)
    {
        for (var i = 0; i < AgentList.Items.Count; i++)
        {
            if (AgentList.Items[i] is Button btn)
            {
                btn.Background = i == index
                    ? new SolidColorBrush(Color.Parse("#2a2d2e"))
                    : Brushes.Transparent;
            }
        }
    }

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SendMessage();
    }

    private void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CancelSending();
    }

    /// <summary>
    /// 取消正在进行的 AI 对话。
    /// </summary>
    private void CancelSending()
    {
        // 用户点 Stop：同步取消所有正在进行的语音/AI/TTS 任务，避免残留。
        try { App.Services.GetRequiredService<IVoiceCoordinator>().CancelEverything(); }
        catch { /* 启动极早期可能未注册，忽略 */ }

        _sendCts?.Cancel();
        var chatService = App.Services.GetRequiredService<AiChatHostedService>();
        chatService.CancelCurrentTask();
    }

    /// <summary>
    /// 切换发送图标 ↔ 转圈+停止 状态。
    /// </summary>
    private void SetSendingState(bool sending)
    {
        _isSending = sending;
        SendButton.IsVisible = !sending;
        StopButton.IsVisible = sending;

        if (sending)
        {
            _spinnerAnimation ??= new Animation
            {
                Duration = TimeSpan.FromSeconds(1),
                IterationCount = IterationCount.Infinite,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0.0) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360.0) } },
                }
            };
            _spinnerAnimation.RunAsync(SpinnerIcon);
            return;
        }
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        if (factory.ChatClient is not null && factory.ChatClient?.MaxContextTokens > 0)
            this.Dispatcher.Invoke(() =>
            {
                var total = (factory.ChatClient?.MaxContextTokens ?? 1) / 1000d;
                var used = (factory.ChatClient?.LastInputTokens ?? 128) / 1000d;
                var percent = used / total * 100;
                AiProgressBar.Value = percent;
                AiProgressBar.Tag = $"{percent:f1}%  {used:f1}k / {total:f0}k";
            });
    }

    /// <summary>
    /// 发送用户消息并触发 AI 对话。
    /// </summary>
    private void SendMessage()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) && _attachments.Count == 0) return;

        // 用户做出"发送"决策：收尾所有进行中的语音/AI/TTS 任务。
        // 这样避免：主窗体下同时存在一条未完成的 STT 轮次 → 在发送后突然 publish OnSttFinal
        // 又打出一条用户气泡、并触发多路 AI 调用。
        try { App.Services.GetRequiredService<IVoiceCoordinator>().CancelEverything(); }
        catch { /* 启动极早期可能未注册，忽略 */ }

        // 隐藏欢迎面板
        HideWelcome();

        // 显示用户消息气泡（保留 #文件名 的显示形式）
        var displayText = text ?? string.Empty;
        if (_attachments.Count > 0)
        {
            var attachNames = string.Join(", ", _attachments.Select(a => $"📎 {a.Name}"));
            displayText = string.IsNullOrEmpty(displayText)
                ? attachNames
                : $"{displayText}\n{attachNames}";
        }
        AddMessageBubble(displayText, isUser: true);
        InputBox.Text = string.Empty;

        // 将 #文件名 替换为完整路径，传给 AI
        var resolvedText = ResolveFileReferences(text ?? string.Empty);
        _fileReferences.Clear();

        // 收集附件并清空 UI
        List<AttachmentInfo>? attachments = _attachments.Count > 0
            ? [.. _attachments]
            : null;
        ClearAttachments();

        // 收集 @智能体 提及并解析位置
        List<AgentMention>? mentions = null;
        if (_agentMentions.Count > 0)
        {
            mentions = [];
            foreach (var (name, agent) in _agentMentions)
            {
                var marker = $"@{name}";
                var idx = resolvedText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    mentions.Add(new AgentMention(agent, idx, idx + marker.Length));
                }
            }
            _agentMentions.Clear();
            if (mentions.Count == 0) mentions = null;
        }

        // 通过 AiChatService 发送，AI 回复将通过 UiChatOutputChannel 渲染到界面
        var chatService = App.Services.GetRequiredService<AiChatHostedService>();
        var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();

        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        HistoryLabel.Text = resolvedText.Truncate(32);
        _ = Task.Run(async () =>
        {
            try
            {
                await chatService.SendMessageAsync(resolvedText, token, attachments, mentions);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("用户取消了对话");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送消息时出错");
                Dispatcher.UIThread.Post(() =>
                    AddMessageBubble($"⚠ 发送失败：{ex.Message}", isUser: false));
            }
            finally
            {

            }
        });
    }

    /// <summary>
    /// 将文本中的 #文件名 替换为完整路径。
    /// </summary>
    private string ResolveFileReferences(string text)
    {
        if (_fileReferences.Count == 0) return text;

        foreach (var (fileName, fullPath) in _fileReferences)
        {
            text = text.Replace($"#{fileName}", fullPath, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private static Bitmap LoadAiAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Cortana/Assets/logo.200.png"));
        return new Bitmap(stream);
    }

    private static Control BuildUserAvatarGlyph(IBrush glyphBrush)
    {
        return new Grid
        {
            Children =
            {
                new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = glyphBrush,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 0, 0),
                },
                new Border
                {
                    Width = 18,
                    Height = 10,
                    CornerRadius = new CornerRadius(9, 9, 6, 6),
                    Background = glyphBrush,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 5),
                }
            }
        };
    }

    /// <summary>
    /// 添加消息气泡到消息列表。
    /// </summary>
    internal void AddMessageBubble(string content, bool isUser, IReadOnlyList<ChatMessageAssetEntity>? assets = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var userAvatarBrush = (IBrush)this.FindResource("UserAvatarBrush")!;
            var userAvatarBorderBrush = (IBrush)this.FindResource("UserAvatarBorderBrush")!;
            var userAvatarGlyphBrush = (IBrush)this.FindResource("UserAvatarGlyphBrush")!;
            var userBubbleBrush = (IBrush)this.FindResource("UserBubbleBrush")!;
            var userBubbleBorderBrush = (IBrush)this.FindResource("UserBubbleBorderBrush")!;
            var aiBubbleBrush = (IBrush)this.FindResource("AiBubbleBrush")!;
            var aiBubbleBorderBrush = (IBrush)this.FindResource("AiBubbleBorderBrush")!;

            var avatar = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                ClipToBounds = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
                Padding = isUser ? new Thickness(0) : new Thickness(1),
                Background = isUser ? userAvatarBrush : Brushes.Transparent,
                BorderBrush = isUser ? userAvatarBorderBrush : aiBubbleBorderBrush,
                BorderThickness = new Thickness(1),
                Child = isUser
                    ? BuildUserAvatarGlyph(userAvatarGlyphBrush)
                    : new Image
                    {
                        Source = AiAvatarBitmap,
                        Width = 40,
                        Height = 40,
                        Stretch = Stretch.UniformToFill,
                    }
            };

            // ── 气泡内容：Markdown + 可选资源卡片 ──
            Control bubbleContent;
            var markdown = new MarkdownRenderer { Markdown = content };

            var hasNonImageAssets = assets is { Count: > 0 } && assets.Any(a => a.AssetGroup != "images");
            if (hasNonImageAssets)
            {
                var appPaths = App.Services.GetRequiredService<IAppPaths>();
                var cardPanel = new ResourceCardPanel(assets!, appPaths.WorkspaceResourcesDirectory);
                bubbleContent = new StackPanel
                {
                    Spacing = 0,
                    Children = { markdown, cardPanel },
                };
            }
            else
            {
                bubbleContent = markdown;
            }

            // ── 消息气泡 ──
            var bubble = new Border
            {
                Background = isUser ? userBubbleBrush : aiBubbleBrush,
                BorderBrush = isUser ? userBubbleBorderBrush : aiBubbleBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = isUser
                    ? new CornerRadius(4, 5, 4, 4)
                    : new CornerRadius(5, 4, 4, 4),
                Padding = new Thickness(14, 10),
                MinWidth = 120,
                // 对面留出头像等宽空白(40+10=50)，确保气泡不会顶到边缘
                Margin = isUser ? new Thickness(50, 0, 0, 0) : new Thickness(0, 0, 50, 0),
                Child = bubbleContent
            };

            // ── 消息行：头像 + 气泡（气泡占满剩余宽度） ──
            var row = new DockPanel
            {
                LastChildFill = true,
            };

            avatar.SetValue(DockPanel.DockProperty, isUser ? Avalonia.Controls.Dock.Right : Avalonia.Controls.Dock.Left);

            if (isUser)
            {
                row.Children.Add(avatar);
                row.Children.Add(bubble);
            }
            else
            {
                row.Children.Add(avatar);
                row.Children.Add(bubble);
            }

            MessageList.Items.Add(row);
            ScrollToBottom();
        });
    }

    /// <summary>
    /// 滚动到消息列表底部（尊重用户手动上滚状态）。
    /// </summary>
    private void ScrollToBottom()
    {
        if (_userScrolledUp) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 自动滚动到底部（流式 token 时调用，尊重用户上滚）。
    /// </summary>
    internal void AutoScrollToBottom()
    {
        if (_userScrolledUp) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 强制滚动到底部（AI 回复结束时调用，重置上滚状态）。
    /// </summary>
    internal void ForceScrollToBottom()
    {
        _userScrolledUp = false;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    // ──────── 窗口操作与事件处理 ────────

    /// <summary>
    /// 会话历史下拉按钮点击 → 弹出/关闭 Popup。
    /// </summary>
    private void OnHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
    }

    /// <summary>
    /// 会话历史列表项点击 → 切换到选中的会话。
    /// </summary>
    private void OnHistoryItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string sessionId)
        {
            var title = btn.Content is TextBlock tb ? tb.Text ?? "新对话" : "新对话";
            HistoryPopup.IsOpen = false;
            SwitchToSession(sessionId, title);
        }
    }

    /// <summary>
    /// 新建会话按钮点击。
    /// </summary>
    private async void OnNewSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await chatEngine.NewSessionAsync();
    }

    /// <summary>
    /// 清空页面消息按钮点击（仅清空 UI 显示，不删除数据库记录）。
    /// </summary>
    private void OnClearMessagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MessageList.Items.Clear();
        ShowWelcome();
    }

    /// <summary>
    /// 智能体选择器按钮点击。
    /// </summary>
    private void OnAgentSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    /// <summary>
    /// 智能体列表项点击。
    /// </summary>
    private void OnAgentItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string agentId)
        {
            AgentSelectorPopup.IsOpen = false;

            var agent = _agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return;

            RefreshAgentDisplay(agent);
            FillAgentSelector(agentId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeAgent(agentId);
        }
    }

    /// <summary>
    /// 厂商选择器按钮点击。
    /// </summary>
    private void OnProviderSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
    }

    /// <summary>
    /// 厂商列表项点击 → 切换厂商并联动加载模型。
    /// </summary>
    private void OnProviderItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string providerId)
        {
            ProviderPopup.IsOpen = false;

            var provider = _providers.FirstOrDefault(p => p.Id == providerId);
            if (provider is null) return;

            _currentProviderId = providerId;
            RefreshProviderDisplay(provider);
            FillProviderSelector(providerId);

            // 持久化为默认厂商并通知 AiChatService
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            providerService.SetDefault(providerId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeProvider(providerId);
            LoadModels(providerId);
        }
    }

    /// <summary>
    /// 模型选择器按钮点击。
    /// </summary>
    private void OnModelSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    /// <summary>
    /// 模型列表项点击。
    /// </summary>
    private void OnModelItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string modelId)
        {
            ModelPopup.IsOpen = false;

            var model = _models.FirstOrDefault(m => m.Id == modelId);
            if (model is null) return;

            RefreshModelDisplay(model);
            FillModelSelector(modelId);

            // 持久化为默认模型并通知 AiChatService
            var modelService = App.Services.GetRequiredService<AiModelService>();
            modelService.SetDefault(modelId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeModel(modelId);
        }
    }

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

    /// <summary>
    /// 强制关闭窗口（退出应用时使用）。
    /// </summary>
    internal void ForceClose()
    {
        _forceClose = true;
        _subscriber?.Dispose();
        Close();
    }
}