using System.Text;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Netor.Cortana.AvaloniaUI.Controls;
using Netor.Cortana.AvaloniaUI.Views;

namespace Netor.Cortana.AvaloniaUI;

/// <summary>
/// Avalonia UI 输出通道。将 AI 流式回复实时渲染到 MainWindow 的消息列表中。
/// 每次对话开始时创建一个 AI 消息气泡，后续 token 累积更新该气泡的 Markdown 内容。
/// </summary>
internal sealed class UiChatOutputChannel(
    IServiceProvider serviceProvider,
    ILogger<UiChatOutputChannel> logger) : IAiOutputChannel
{
    private static readonly Bitmap AiAvatarBitmap = LoadAiAvatarBitmap();

    private readonly StringBuilder _buffer = new();
    private MarkdownRenderer? _currentPresenter;

    private static Bitmap LoadAiAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Cortana/Assets/logo.200.png"));
        return new Bitmap(stream);
    }

    /// <inheritdoc />
    public string Name => "AvaloniaUI";

    /// <inheritdoc />
    public bool IsActive => true;

    /// <inheritdoc />
    public Task OnTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        _buffer.Append(token);
        var currentText = _buffer.ToString();

        Dispatcher.UIThread.Post(() =>
        {
            EnsureBubbleCreated();
            if (_currentPresenter is not null)
            {
                _currentPresenter.Markdown = currentText;
            }

            // 流式 token 期间自动跟随滚动（尊重用户上滚）
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.AutoScrollToBottom();
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnDoneAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var finalText = _buffer.ToString();

        Dispatcher.UIThread.Post(() =>
        {
            // 最终刷新一次确保完整内容（跳过防抖立即渲染）
            if (_currentPresenter is not null && !string.IsNullOrEmpty(finalText))
            {
                _currentPresenter.FlushRender();
                _currentPresenter.Markdown = finalText;
            }

            ScrollToBottom(force: true);

            // 必须在 UI 线程回调内重置，否则 Post 异步导致 _currentPresenter 提前被置空
            Reset();
        });

        logger.LogDebug("UI 输出通道完成，Session：{SessionId}", sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnCancelledAsync()
    {
        logger.LogDebug("UI 输出通道已取消");
        Dispatcher.UIThread.Post(Reset);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.AddMessageBubble($"⚠ {message}", isUser: false);
        });

        logger.LogWarning("UI 输出通道收到错误：{Message}", message);
        Reset();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 确保已创建 AI 回复气泡。首次收到 token 时创建。
    /// 布局与 MainWindow.AddMessageBubble 保持一致：DockPanel(头像 + 头部信息 + 气泡)。
    /// </summary>
    private void EnsureBubbleCreated()
    {
        if (_currentPresenter is not null) return;

        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        var messageList = mainWindow.FindControl<ItemsControl>("MessageList");
        if (messageList is null) return;

        var aiBubbleBrush = (IBrush)mainWindow.FindResource("AiBubbleBrush")!;
        var aiBubbleBorderBrush = (IBrush)mainWindow.FindResource("AiBubbleBorderBrush")!;
        var subtextBrush = (IBrush)mainWindow.FindResource("SubtextBrush")!;

        // ── AI 头像 ──
        var avatar = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = aiBubbleBorderBrush,
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = AiAvatarBitmap,
                Width = 40,
                Height = 40,
                Stretch = Stretch.UniformToFill,
            }
        };

        // ── 气泡头部：智能体名称 + 时间 ──
        var agentLabel = mainWindow.FindControl<TextBlock>("ToolbarAgentLabel");
        var displayName = agentLabel?.Text ?? "助手";
        var displayTime = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm");

        var headerPanel = new DockPanel
        {
            Margin = new Thickness(0, 0, 50, 2),
        };
        var nameBlock = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = subtextBrush,
        };
        var timeBlock = new TextBlock
        {
            Text = displayTime,
            FontSize = 10,
            Foreground = subtextBrush,
            Opacity = 0.7,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameBlock.SetValue(DockPanel.DockProperty, Dock.Left);
        timeBlock.SetValue(DockPanel.DockProperty, Dock.Left);
        headerPanel.Children.Add(nameBlock);
        headerPanel.Children.Add(timeBlock);

        _currentPresenter = new MarkdownRenderer
        {
            Markdown = "",
        };

        // ── 气泡容器 ──
        var bubble = new Border
        {
            Background = aiBubbleBrush,
            BorderBrush = aiBubbleBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 3, 3, 3),
            Padding = new Thickness(14, 10),
            MinWidth = 120,
            Margin = new Thickness(0, 0, 50, 0),
            Child = _currentPresenter,
        };

        // ── 消息行：头像(Left) + (头部信息 + 气泡)(Fill) ──
        var bubbleColumn = new StackPanel { Spacing = 0 };
        bubbleColumn.Children.Add(headerPanel);
        bubbleColumn.Children.Add(bubble);

        var row = new DockPanel { LastChildFill = true };
        avatar.SetValue(DockPanel.DockProperty, Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(bubbleColumn);

        messageList.Items.Add(row);
        ScrollToBottom();
    }

    /// <summary>
    /// 滚动消息列表到底部。
    /// </summary>
    private void ScrollToBottom(bool force = false)
    {
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        if (force)
            mainWindow.ForceScrollToBottom();
        else
            mainWindow.AutoScrollToBottom();
    }

    /// <summary>
    /// 重置通道状态，为下一轮对话做准备。
    /// </summary>
    private void Reset()
    {
        _buffer.Clear();
        _currentPresenter = null;
    }
}
