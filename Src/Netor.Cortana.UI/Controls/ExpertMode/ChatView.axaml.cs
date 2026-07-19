using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls.Common;
using Netor.Cortana.UI.Controls.Shared;
using Netor.Cortana.UI.ViewModels.ExpertMode;

using System.ComponentModel;
using System.Text;

namespace Netor.Cortana.UI.Controls.ExpertMode;

public enum ChatContentSide
{
    User,
    Assistant
}

/// <summary>
/// 专家（Chat）模式视图。
///
/// 职责：
/// - 持有消息列表（MessageList / WelcomePanel / ScrollToBottomBtn）
/// - 提供公共 API 供 MainWindow 调用（AddMessageBubble / AddSystemNotice / AddRealtimeProcessCard）
/// - 持有 ChatInputArea（InputAreaView），由构造函数注入 ChatInputVm
/// - 提供 Chat→Workflow 建议 Banner（由 MainWindow 控制 ShowWorkflowSuggestion）
///
/// DataContext：不绑定 VM（内部元素 code-behind 手动管理）。
/// </summary>
public partial class ChatView : UserControl
{
    private const double MessageAvatarSize = 40;
    private const double MessageAvatarGap = 10;
    private const double MessageAvatarSlotWidth = MessageAvatarSize + MessageAvatarGap;

    private static readonly Bitmap AiAvatarBitmap = LoadAiAvatarBitmap();
    private static readonly Bitmap UserAvatarBitmap = LoadUserAvatarBitmap();
    private static readonly Bitmap NoticeToggleBitmap = LoadNoticeToggleBitmap();

    // 每个 agent 的头像 Bitmap 缓存（键：AgentEntity.Id）
    private readonly Dictionary<string, Bitmap> _agentAvatarCache = new();

    private bool _userScrolledUp;

    // 用于通知 MainWindow 用户点击了 Banner 按钮（切到工作模式 / 忽略）
    public event Action<string?, string?>? WorkflowSuggestionAccepted;
    public event Action? WorkflowSuggestionDismissed;

    // 当前展示的建议数据
    private string? _pendingSuggestionInput;
    private string? _pendingSuggestionSubMode;

    public ChatView()
    {
        InitializeComponent();
        WelcomeLogoImage.Source = LoadAiAvatarBitmap();

        // 注入 ChatInputVm
        var chatInputVm = App.Services.GetRequiredService<ChatInputVm>();
        DataContext = chatInputVm;
        ChatInputArea.SetInputVm(chatInputVm);
        WireOrchestrationDiagnosticsPanel(chatInputVm);

        MessageScroller.ScrollChanged += OnScrollChanged;

        AttachedToVisualTree += (_, _) =>
        {
            // 初始状态：欢迎面板可见
            ShowWelcome();
        };
    }

    // ──── 公共 API（供 MainWindow 调用） ────

    /// <summary>获取消息列表控件。</summary>
    public ItemsControl Messages => MessageList;

    /// <summary>获取当前选中智能体名称（供 UiChatOutputChannel 显示气泡头部）。</summary>
    public string CurrentAgentName
    {
        get
        {
            var chatInputVm = App.Services.GetRequiredService<ViewModels.ExpertMode.ChatInputVm>();
            return chatInputVm.SelectedAgentName;
        }
    }

    /// <summary>创建一条可流式更新的 AI 气泡，并返回其 Markdown 呈现器。</summary>
    public MarkdownRenderer CreateStreamingAssistantBubble()
    {
        HideWelcome();

        var markdown = new MarkdownRenderer { Markdown = string.Empty };
        var chatInputVm = App.Services.GetRequiredService<ChatInputVm>();
        var row = BuildMessageRow(markdown, isUser: false, authorName: null, timestamp: DateTimeOffset.Now,
            agentAvatarOverride: ResolveAgentAvatar(chatInputVm.SelectedAgent));
        MessageList.Items.Add(row);
        AutoScrollToBottom();
        return markdown;
    }

    /// <summary>刷新 Token 进度条（由 MainWindow 订阅 AIAgentFactory.TokenUsageChanged 后调用）。</summary>
    public void RefreshTokenProgress(long usedTokens, long maxTokens)
    {
        if (AiProgressBar is null || maxTokens <= 0) return;
        var total = maxTokens / 1000d;
        var used = usedTokens / 1000d;
        var percent = total > 0 ? used / total * 100 : 0;
        AiProgressBar.Value = percent;
        AiProgressBar.Tag = usedTokens > 0
            ? $"{percent:f1}%  {used:f1}k / {total:f0}k"
            : $"0%  0k / {total:f0}k";
        ToolTip.SetTip(AiProgressBar, AiProgressBar.Tag);
    }

    /// <summary>接受外部文件路径，添加到 ChatInputVm.Attachments（供 LeftPanel 文件树拖入）。</summary>
    public void AddExternalAttachments(IReadOnlyList<string> filePaths)
    {
        ChatInputArea?.AddExternalAttachments(filePaths);
    }

    /// <summary>清空消息列表并显示欢迎面板。</summary>
    public void Clear()
    {
        MessageList.Items.Clear();
        ShowWelcome();
    }

    /// <summary>
    /// 切换会话后刷新开发态编排诊断条。
    /// </summary>
    public void RefreshOrchestrationDiagnostics()
    {
        if (DataContext is ChatInputVm chatInputVm)
        {
            chatInputVm.RefreshDiagnosticsForCurrentSession();
            SyncOrchestrationDiagnosticsPanelVisibility(chatInputVm);
        }
    }

    /// <summary>显示欢迎面板。</summary>
    public void ShowWelcome() => WelcomePanel.IsVisible = true;

    /// <summary>隐藏欢迎面板。</summary>
    public void HideWelcome() => WelcomePanel.IsVisible = false;

    /// <summary>强制滚动到底部（重置上滚状态）。</summary>
    public void ForceScrollToBottom()
    {
        _userScrolledUp = false;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>自动滚动到底部（尊重用户上滚状态）。</summary>
    public void AutoScrollToBottom()
    {
        if (_userScrolledUp) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>在消息列表末尾追加一次布局完成后的滚动到底部（加载历史消息时使用）。</summary>
    public void ScrollToBottomOnNextLayout()
    {
        _userScrolledUp = false;
        MessageList.LayoutUpdated += ScrollOnceAfterLayout;
        void ScrollOnceAfterLayout(object? s, System.EventArgs e)
        {
            MessageList.LayoutUpdated -= ScrollOnceAfterLayout;
            Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 显示 Chat→Workflow 建议 Banner。
    /// </summary>
    public void ShowWorkflowSuggestion(string reason, string inputPreview, string? subMode, string? pendingInput)
    {
        _pendingSuggestionInput = pendingInput;
        _pendingSuggestionSubMode = subMode;
        WorkflowSuggestionReason.Text = reason;
        WorkflowSuggestionInputPreview.Text = inputPreview;
        WorkflowSuggestionBanner.IsVisible = true;
    }

    /// <summary>隐藏 Chat→Workflow 建议 Banner。</summary>
    public void HideWorkflowSuggestion()
    {
        WorkflowSuggestionBanner.IsVisible = false;
        _pendingSuggestionInput = null;
        _pendingSuggestionSubMode = null;
    }

    // ──── 滚动控制 ────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = MessageScroller;
        _userScrolledUp = sv.Offset.Y + sv.Viewport.Height < sv.Extent.Height - 30;
        ScrollToBottomBtn.IsVisible = _userScrolledUp;
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        ForceScrollToBottom();
    }

    // ──── Banner 按钮 ────

    private void OnWorkflowSuggestionAcceptClick(object? sender, RoutedEventArgs e)
    {
        WorkflowSuggestionBanner.IsVisible = false;
        WorkflowSuggestionAccepted?.Invoke(_pendingSuggestionInput, _pendingSuggestionSubMode);
    }

    private void OnWorkflowSuggestionDismissClick(object? sender, RoutedEventArgs e)
    {
        WorkflowSuggestionBanner.IsVisible = false;
        WorkflowSuggestionDismissed?.Invoke();
    }

    private void OnOrchestrationDiagnosticsToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatInputVm chatInputVm)
        {
            chatInputVm.IsOrchestrationDiagnosticsExpanded = !chatInputVm.IsOrchestrationDiagnosticsExpanded;
        }
    }

    private void OnAllOrchestrationDiagnosticsFilterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatInputVm chatInputVm)
        {
            chatInputVm.OrchestrationDiagnosticsFilter = OrchestrationDiagnosticsFilter.All;
        }
    }

    private void OnWarningOrchestrationDiagnosticsFilterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatInputVm chatInputVm)
        {
            chatInputVm.OrchestrationDiagnosticsFilter = OrchestrationDiagnosticsFilter.WarningOnly;
        }
    }

    private void OnFailedOrchestrationDiagnosticsFilterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatInputVm chatInputVm)
        {
            chatInputVm.OrchestrationDiagnosticsFilter = OrchestrationDiagnosticsFilter.FailedOnly;
        }
    }

    private void WireOrchestrationDiagnosticsPanel(ChatInputVm chatInputVm)
    {
#if DEBUG
        SyncOrchestrationDiagnosticsPanelVisibility(chatInputVm);
        chatInputVm.PropertyChanged += OnChatInputVmPropertyChanged;
#else
        OrchestrationDiagnosticsPanel.IsVisible = false;
#endif
    }

    private void OnChatInputVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
#if DEBUG
        if (e.PropertyName is nameof(ChatInputVm.ShowOrchestrationDiagnostics)
            && sender is ChatInputVm chatInputVm)
        {
            SyncOrchestrationDiagnosticsPanelVisibility(chatInputVm);
        }
#endif
    }

    private void SyncOrchestrationDiagnosticsPanelVisibility(ChatInputVm chatInputVm)
    {
#if DEBUG
        OrchestrationDiagnosticsPanel.IsVisible = chatInputVm.ShowOrchestrationDiagnostics;
#else
        OrchestrationDiagnosticsPanel.IsVisible = false;
#endif
    }

    private async void OnCopyOrchestrationDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatInputVm chatInputVm)
        {
            return;
        }

        var text = chatInputVm.ExportOrchestrationDiagnosticsText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        AddSystemNotice(new SystemNoticeArgs(
            "当前会话编排诊断已复制到剪贴板。",
            "诊断已复制",
            "success",
            "调试",
            DateTimeOffset.Now));
    }

    private async void OnCopyTurnDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatInputVm chatInputVm)
        {
            return;
        }

        if (sender is not Control { DataContext: ChatTurnDiagnosticsItemVm item })
        {
            return;
        }

        var text = chatInputVm.ExportTurnDiagnosticsText(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        AddSystemNotice(new SystemNoticeArgs(
            $"Turn {item.TurnId} 的编排诊断已复制到剪贴板。",
            "单轮诊断已复制",
            "success",
            "调试",
            DateTimeOffset.Now));
    }

    // ──── 消息气泡渲染 ────

    /// <summary>
    /// 添加消息气泡到消息列表（从 MainWindow.Messaging.cs 迁移）。
    /// </summary>
    public void AddMessageBubble(string content, bool isUser,
        IReadOnlyList<ChatMessageAssetEntity>? assets = null,
        string? authorName = null, DateTimeOffset? timestamp = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HideWelcome();

            // 气泡内容（Markdown + 可选资源卡片）
            var markdownContent = assets is { Count: > 0 }
                ? AppendImageAssetPreviewsForLegacyPaths(content, assets)
                : content;
            var markdown = new MarkdownRenderer { Markdown = markdownContent };
            Control bubbleContent;
            var hasNonImageAssets = assets is { Count: > 0 } && assets.Any(a => a.AssetGroup != "images");
            if (hasNonImageAssets)
            {
                var appPaths = App.Services.GetRequiredService<IAppPaths>();
                var cardPanel = new ResourceCardPanel(assets!, appPaths.WorkspaceResourcesDirectory);
                bubbleContent = new StackPanel { Spacing = 0, Children = { markdown, cardPanel } };
            }
            else
            {
                bubbleContent = markdown;
            }

            // 历史消息：助手气泡按 authorName 查找智能体并解析头像；未设置时回退品牌 Logo
            var agentAvatar = isUser ? null : ResolveAgentAvatarByName(authorName);
            MessageList.Items.Add(BuildMessageRow(bubbleContent, isUser, authorName, timestamp, agentAvatar));
            AutoScrollToBottom();
        });
    }

    /// <summary>
    /// 添加临时系统提示卡片（从 MainWindow.Messaging.cs 迁移）。
    /// </summary>
    public void AddSystemNotice(SystemNoticeArgs args)
    {
        const int collapsedLineCount = 2;

        Dispatcher.UIThread.Post(() =>
        {
            var content = args.Content.Trim();
            var contentLines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var collapsed = contentLines.Length > collapsedLineCount;
            var collapsedContent = collapsed
                ? $"{string.Join(Environment.NewLine, contentLines.Take(collapsedLineCount))}{Environment.NewLine}…"
                : content;
            var displayContent = collapsed ? collapsedContent : content;
            var title = string.IsNullOrWhiteSpace(args.Title) ? "系统提示" : args.Title.Trim();
            var source = string.IsNullOrWhiteSpace(args.Source) ? "系统" : args.Source.Trim();
            var displayTime = args.CreatedAt.ToLocalTime().ToString("HH:mm");
            var headerTitle = string.IsNullOrWhiteSpace(source) ? title : $"{title} · {source}";

            var subtextBrush = (IBrush)this.FindResource("SubtextBrush")!;

            var arrowImage = new Image
            {
                Source = NoticeToggleBitmap,
                Width = 13, Height = 13, Opacity = 0.58,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new RotateTransform(collapsed ? 90 : 180),
            };

            var titleBlock = new TextBlock
            {
                Text = headerTitle, FontSize = 12, FontWeight = FontWeight.SemiBold,
                Foreground = subtextBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var metaBlock = new TextBlock
            {
                Text = displayTime, FontSize = 10,
                Foreground = subtextBrush, Opacity = 0.75,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var leftHeader = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7,
                Children = { arrowImage, titleBlock },
            };
            var headerPanel = new DockPanel { LastChildFill = true };
            leftHeader.SetValue(DockPanel.DockProperty, Dock.Left);
            metaBlock.SetValue(DockPanel.DockProperty, Dock.Right);
            headerPanel.Children.Add(leftHeader);
            headerPanel.Children.Add(metaBlock);

            var headerButton = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = headerPanel,
            };

            var header = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 6),
                Child = headerButton,
            };

            var contentBlock = new SelectableTextBlock
            {
                Text = displayContent, FontSize = 12,
                Foreground = subtextBrush, TextWrapping = TextWrapping.Wrap, Opacity = 0.92,
            };

            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(header);
            body.Children.Add(contentBlock);

            if (collapsed)
            {
                var expanded = false;
                headerButton.Click += (_, _) =>
                {
                    expanded = !expanded;
                    contentBlock.Text = expanded ? content : collapsedContent;
                    if (arrowImage.RenderTransform is RotateTransform rt)
                        rt.Angle = expanded ? 180 : 90;
                };
            }
            else
            {
                headerButton.Cursor = new Cursor(StandardCursorType.Arrow);
            }

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 7),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Child = body,
            };

            MessageList.Items.Add(WrapAssistantSideContent(card));
            AutoScrollToBottom();
        });
    }

    /// <summary>添加实时进程卡片（RealtimeProcessCard）。</summary>
    public RealtimeProcessCardHandle AddRealtimeProcessCard(RealtimeProcessEvent initial)
    {
        var card = new RealtimeProcessCard(initial);
        NormalizeStandaloneCardMargin(card);
        MessageList.Items.Add(WrapUserSideContent(card, new Thickness(0, -18, 0, 0)));
        AutoScrollToBottom();
        return new RealtimeProcessCardHandle(card);
    }

    public ToolProcessGroupCard AddToolProcessGroupCard(string groupId, ChatContentSide side)
    {
        var card = new ToolProcessGroupCard(groupId);
        NormalizeStandaloneCardMargin(card);
        var row = side == ChatContentSide.Assistant
            ? WrapAssistantSideContent(card, new Thickness(0, -18, 0, 0))
            : WrapUserSideContent(card, new Thickness(0, -18, 0, 0));
        MessageList.Items.Add(row);
        AutoScrollToBottom();
        return card;
    }

    private Control BuildMessageRow(Control bubbleContent, bool isUser, string? authorName, DateTimeOffset? timestamp,
        Bitmap? agentAvatarOverride = null)
    {
        var userAvatarBrush = (IBrush)this.FindResource("UserAvatarBrush")!;
        var userAvatarBorderBrush = (IBrush)this.FindResource("UserAvatarBorderBrush")!;
        var userAvatarGlyphBrush = (IBrush)this.FindResource("UserAvatarGlyphBrush")!;
        var userBubbleBrush = (IBrush)this.FindResource("UserBubbleBrush")!;
        var userBubbleBorderBrush = (IBrush)this.FindResource("UserBubbleBorderBrush")!;
        var aiBubbleBrush = (IBrush)this.FindResource("AiBubbleBrush")!;
        var aiBubbleBorderBrush = (IBrush)this.FindResource("AiBubbleBorderBrush")!;
        var aiAvatarBorderBrush = (IBrush)this.FindResource("AiAvatarBorderBrush")!;
        var subtextBrush = (IBrush)this.FindResource("SubtextBrush")!;

        var avatar = new Border
        {
            Width = MessageAvatarSize,
            Height = MessageAvatarSize,
            CornerRadius = new CornerRadius(MessageAvatarSize / 2),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = isUser
                ? new Thickness(MessageAvatarGap, 0, 0, 0)
                : new Thickness(0, 0, MessageAvatarGap, 0),
            Padding = isUser ? new Thickness(0) : new Thickness(1),
            Background = isUser ? userAvatarBrush : Brushes.Transparent,
            BorderBrush = isUser ? userAvatarBorderBrush : aiAvatarBorderBrush,
            BorderThickness = new Thickness(1),
            Child = isUser
                ? BuildUserAvatarGlyph(userAvatarGlyphBrush)
                : new Image
                {
                    // 优先使用智能体自定义头像；未设置或加载失败时回退品牌 Logo
                    Source = agentAvatarOverride ?? AiAvatarBitmap,
                    Width = MessageAvatarSize,
                    Height = MessageAvatarSize,
                    Stretch = Stretch.UniformToFill,
                },
        };
        var displayName = authorName ?? (isUser ? "用户" : CurrentAgentName);
        var displayTime = (timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm");
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = isUser
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left,
            Children =
            {
                new TextBlock
                {
                    Text = displayName,
                    FontSize = 11,
                    FontWeight = FontWeight.Medium,
                    Foreground = subtextBrush,
                },
                new TextBlock
                {
                    Text = displayTime,
                    FontSize = 10,
                    Opacity = 0.6,
                    Foreground = subtextBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                },
            }
        };

        var headerPanel = new DockPanel
        {
            LastChildFill = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 2),
        };
        headerRow.SetValue(DockPanel.DockProperty, isUser ? Dock.Right : Dock.Left);
        headerPanel.Children.Add(headerRow);

        var bubble = new Border
        {
            Background = isUser ? userBubbleBrush : aiBubbleBrush,
            BorderBrush = isUser ? userBubbleBorderBrush : aiBubbleBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = isUser ? new CornerRadius(4, 5, 4, 4) : new CornerRadius(5, 4, 4, 4),
            Padding = new Thickness(14, 10),
            MinWidth = 120,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = bubbleContent,
        };

        var bubbleColumn = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        bubbleColumn.Children.Add(headerPanel);
        bubbleColumn.Children.Add(bubble);

        var row = new DockPanel { LastChildFill = true };
        avatar.SetValue(DockPanel.DockProperty, isUser ? Dock.Right : Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(bubbleColumn);
        return row;
    }

    private Control WrapUserSideContent(Control content, Thickness? margin = null)
    {
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        var spacer = new Border
        {
            Width = MessageAvatarSlotWidth,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = margin ?? default,
        };

        spacer.SetValue(DockPanel.DockProperty, Dock.Right);
        row.Children.Add(spacer);
        row.Children.Add(content);
        return row;
    }

    private Control WrapAssistantSideContent(Control content, Thickness? margin = null)
    {
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        var spacer = new Border
        {
            Width = MessageAvatarSlotWidth,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = margin ?? default,
        };

        spacer.SetValue(DockPanel.DockProperty, Dock.Left);
        row.Children.Add(spacer);
        row.Children.Add(content);
        return row;
    }

    private static void NormalizeStandaloneCardMargin(Control card)
    {
        card.Margin = new Thickness(0);
        if (card.FindControl<Border>("RootBorder") is { } rootBorder)
        {
            rootBorder.Margin = new Thickness(0);
        }
    }

    // ──── 智能体头像解析 ────

    /// <summary>
    /// 解析智能体头像 Bitmap。
    /// 若智能体未设置头像、文件不存在或加载失败，一律回退到品牌 Logo（<see cref="AiAvatarBitmap"/>）。
    /// 结果按 AgentEntity.Id 缓存，避免重复 IO。
    /// </summary>
    private Bitmap ResolveAgentAvatar(AgentEntity? agent)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.Avatar))
            return AiAvatarBitmap;

        if (_agentAvatarCache.TryGetValue(agent.Id, out var cached))
            return cached;

        try
        {
            var appPaths = App.Services.GetRequiredService<IAppPaths>();
            var avatarPath = Path.Combine(appPaths.UserAgentsDirectory, agent.Id, agent.Avatar);
            if (File.Exists(avatarPath))
            {
                var bitmap = new Bitmap(avatarPath);
                _agentAvatarCache[agent.Id] = bitmap;
                return bitmap;
            }
        }
        catch
        {
            // 路径异常或解码失败时安全回退，不向外抛异常
        }

        return AiAvatarBitmap;
    }

    /// <summary>
    /// 按智能体显示名称（AuthorName）查找实体并解析头像。
    /// 用于历史消息加载场景。名称为空或查找不到时回退到品牌 Logo。
    /// </summary>
    private Bitmap ResolveAgentAvatarByName(string? authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName))
            return AiAvatarBitmap;

        try
        {
            var agentService = App.Services.GetRequiredService<AgentService>();
            var agent = agentService.FindByNameOrDisplayName(authorName);
            return ResolveAgentAvatar(agent);
        }
        catch
        {
            return AiAvatarBitmap;
        }
    }

    // ──── 静态资源加载 ────

    private static Bitmap LoadAiAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.LogoImageFileName)));
        return new Bitmap(stream);
    }

    private static Bitmap LoadUserAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri("mk.png")));
        return new Bitmap(stream);
    }

    private static Bitmap LoadNoticeToggleBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri("up.png")));
        return new Bitmap(stream);
    }

    private static Control BuildUserAvatarGlyph(IBrush glyphBrush)
    {
        return new Image
        {
            Source = UserAvatarBitmap, Width = 32, Height = 32,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    private static string AppendImageAssetPreviewsForLegacyPaths(string content, IReadOnlyList<ChatMessageAssetEntity> assets)
    {
        var imageAssets = assets.Where(static asset => asset.AssetGroup == "images").ToArray();
        if (imageAssets.Length == 0 || content.Contains("file://", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var appPaths = App.Services.GetRequiredService<IAppPaths>();
        var builder = new StringBuilder(content ?? string.Empty);
        foreach (var asset in imageAssets)
        {
            if (string.IsNullOrWhiteSpace(asset.RelativePath))
            {
                continue;
            }

            var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, asset.RelativePath);
            if (!File.Exists(absolutePath))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"![{asset.OriginalName}]({new Uri(Path.GetFullPath(absolutePath)).AbsoluteUri})");
        }

        return builder.ToString();
    }
}
