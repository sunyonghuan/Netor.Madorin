using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.UI.Controls.Common;
using Netor.Cortana.UI.ViewModels.Meeting;

namespace Netor.Cortana.UI.Controls.Meeting;

/// <summary>
/// 会议模式主视图，负责承载会议消息流、状态提示、成员条和独立输入区。
/// </summary>
public partial class MeetingView : UserControl
{
    private MeetingViewController? _controller;
    private MeetingInputVm? _inputVm;
    private bool _userScrolledUp;

    public MeetingView()
    {
        InitializeComponent();
        WelcomeLogoImage.Source = LoadBrandLogoBitmap();

        _inputVm = App.Services.GetService<MeetingInputVm>();
        if (_inputVm is not null)
        {
            MeetingInputArea.SetInputVm(_inputVm);
            _inputVm.UserMessageSubmitted += AppendOptimisticUserBubble;
        }

        AttachedToVisualTree += (_, _) => EnsureController();
    }

    /// <summary>
    /// 从品牌默认入口加载欢迎页 Logo。
    /// </summary>
    private static Bitmap LoadBrandLogoBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.LogoImageFileName)));
        return new Bitmap(stream);
    }

    internal ItemsControl Messages => MessageList;

    internal void SetVisibility(bool isVisible)
    {
        EnsureController();
        _controller?.SetVisibility(isVisible);
    }

    internal Task LoadMeetingHistoryAsync(string meetingId)
    {
        EnsureController();
        return _controller?.LoadMeetingHistoryAsync(meetingId) ?? Task.CompletedTask;
    }

    internal void SetActiveMeeting(string? meetingId)
    {
        if (!string.Equals(_inputVm?.ActiveMeetingId, meetingId, StringComparison.Ordinal))
        {
            _inputVm?.SetActiveMeeting(meetingId);
        }
    }

    internal void StartNewMeeting()
    {
        EnsureController();
        _controller?.StartNewMeeting();
    }

    internal void ClearMessages()
    {
        MessageList.Items.Clear();
        WelcomePanel.IsVisible = true;
        MembersStrip.Items.Clear();
        MembersStrip.IsVisible = false;
    }

    internal void AddBubble(MeetingBubbleHandle handle)
    {
        WelcomePanel.IsVisible = false;
        MessageList.Items.Add(handle.Root);
        AutoScrollToBottom();
    }

    internal Control AddProcessCard(RealtimeProcessCard card)
    {
        WelcomePanel.IsVisible = false;
        var container = new Border
        {
            Margin = new Thickness(0, -18, 0, 0),
            Child = card,
        };
        MessageList.Items.Add(container);
        AutoScrollToBottom();
        return container;
    }

    internal Control AddToolProcessGroupCard(ToolProcessGroupCard card)
    {
        WelcomePanel.IsVisible = false;
        var container = new Border
        {
            Margin = new Thickness(0, -18, 0, 0),
            Child = card,
        };
        MessageList.Items.Add(container);
        AutoScrollToBottom();
        return container;
    }

    internal void AddSystemMessage(string text)
    {
        WelcomePanel.IsVisible = false;
        MessageList.Items.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(50, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.Parse("#8f8f8f")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            },
        });
        AutoScrollToBottom();
    }

    internal void SetStatus(string text, string color)
    {
        StatusHintBar.Text = text;
        StatusHintBar.Foreground = new SolidColorBrush(Color.Parse(color));
    }

    internal void SetReadOnly(bool isReadOnly)
    {
        MeetingInputArea.IsEnabled = !isReadOnly;
    }

    internal void SetMeetingRunning(string meetingId, bool isRunning)
    {
        _inputVm?.MarkMeetingRunning(meetingId, isRunning);
    }

    internal void SetMeetingIdle(string meetingId)
    {
        _inputVm?.MarkMeetingIdle(meetingId);
    }

    internal void RenderMembers(IEnumerable<string> memberNames)
    {
        MembersStrip.Items.Clear();

        foreach (var memberName in memberNames.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            MembersStrip.Items.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3c3c3c")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(0, 0, 6, 4),
                Child = new TextBlock
                {
                    Text = memberName,
                    Foreground = new SolidColorBrush(Color.Parse("#a0a0a0")),
                    FontSize = 12,
                },
            });
        }

        MembersStrip.IsVisible = MembersStrip.Items.Count > 0;
    }

    internal void AutoScrollToBottom()
    {
        if (_userScrolledUp)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    internal void ForceScrollToBottom()
    {
        _userScrolledUp = false;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    internal void ScrollToBottomOnNextLayout()
    {
        _userScrolledUp = false;
        MessageList.LayoutUpdated += ScrollOnceAfterLayout;

        void ScrollOnceAfterLayout(object? sender, EventArgs args)
        {
            MessageList.LayoutUpdated -= ScrollOnceAfterLayout;
            Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void EnsureController()
    {
        if (_controller is not null)
        {
            return;
        }

        var subscriber = App.Services.GetService<ISubscriber>();
        var executor = App.Services.GetService<MeetingExecutor>();
        var sessions = App.Services.GetService<MeetingSessionService>();
        var messages = App.Services.GetService<MeetingMessageService>();
        var agentFiles = App.Services.GetService<AgentFileService>();
        var sessionResolver = App.Services.GetService<ICurrentSessionResolver>();

        if (subscriber is null || executor is null || sessions is null || messages is null || agentFiles is null || sessionResolver is null)
        {
            SetStatus("会议模式初始化失败", "#f48771");
            return;
        }

        _controller = new MeetingViewController(this, subscriber, executor, sessions, messages, agentFiles, sessionResolver);
        _inputVm?.BindController(_controller);
    }

    private void AppendOptimisticUserBubble(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var handle = MeetingBubbleBuilder.Build(new MeetingBubbleArgs(
                Guid.NewGuid().ToString("N"),
                "user",
                "user",
                "老板",
                text,
                "normal",
                DateTimeOffset.Now));
            AddBubble(handle);
        });
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var isAtBottom = MessageScroller.Offset.Y + MessageScroller.Viewport.Height >= MessageScroller.Extent.Height - 30;
        _userScrolledUp = !isAtBottom;
        ScrollToBottomBtn.IsVisible = _userScrolledUp;
    }

    private void OnScrollToBottomClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ForceScrollToBottom();
    }
}
