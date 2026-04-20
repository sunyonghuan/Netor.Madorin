using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.EventHub;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// 唤醒词触发后显示的字幕气泡窗口（纯 XAML 实现）。
/// 显示在浮动窗口上方，跟随 FloatWindow 移动。
/// 纯事件驱动：通过 EventHub 订阅语音流程事件控制显示/更新/关闭。
/// </summary>
public partial class BubbleWindow : Window
{
    private const int BubbleWidth = 200;
    private const int BubbleHeight = 56;
    private const int GapFromFloat = 10;

    /// <summary>STT 自然结束 / 用户停顿后，气泡保留多久再隐藏。</summary>
    private static readonly TimeSpan DismissAfterStopped = TimeSpan.FromMilliseconds(1500);

    /// <summary>主窗体可见时收到 STT Final，气泡只短暂展示再隐藏（AI 走主窗体，无 TTS 接力）。</summary>
    private static readonly TimeSpan DismissAfterFinalInMainMode = TimeSpan.FromMilliseconds(1000);

    /// <summary>气泡硬超时：兜底防止某次事件丢失导致气泡常驻。</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>系统通知在气泡上的停留时长。</summary>
    private static readonly TimeSpan DismissAfterSystemNotification = TimeSpan.FromMilliseconds(2600);

    private FloatWindow? _anchorWindow;
    private IWindowController? _windowController;

    private enum BubbleState { Hidden, Showing, Dismissing }

    private BubbleState _state = BubbleState.Hidden;
    private DispatcherTimer? _dismissTimer;
    private DispatcherTimer? _hardTimeoutTimer;

    private ILogger<BubbleWindow> Logger => App.Services.GetRequiredService<ILogger<BubbleWindow>>();
    private IWindowController WindowController =>
        _windowController ??= App.Services.GetRequiredService<IWindowController>();

    public BubbleWindow()
    {
        InitializeComponent();
        SubscribeEvents();
    }

    /// <summary>
    /// 设置锚点窗口引用，Bubble 将跟随其位置显示。
    /// </summary>
    internal void SetAnchorWindow(FloatWindow floatWindow)
    {
        _anchorWindow = floatWindow;
    }

    /// <summary>
    /// 订阅语音流程 EventHub 事件，驱动 Bubble 生命周期。
    /// </summary>
    private void SubscribeEvents()
    {
        var subscriber = App.Services.GetRequiredService<ISubscriber>();

        // 唤醒词 → 显示 Bubble，进入监听状态
        subscriber.On(Events.OnWakeWordDetected, (_, _) =>
        {
            Logger.LogDebug("收到唤醒词事件，显示气泡窗口。");
            PostToUI(ShowBubble);
            return Task.FromResult(false);
        });

        // STT 中间结果 → 更新字幕（保持气泡可见，取消任何挂起的 dismiss）
        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, (_, args) =>
        {
            PostToUI(() =>
            {
                ShowBubble();
                UpdateSubtitle(args.Text);
            });
            return Task.FromResult(false);
        });

        // STT 最终结果 → 字幕定格
        // - Bubble 模式：等待 TTS 接力（TtsStarted 会再次 ShowBubble，自动取消 dismiss）
        // - MainWindow 模式：AI 走主窗体不走 TTS，1 秒后自动收起气泡
        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, args) =>
        {
            PostToUI(() =>
            {
                ShowBubble();
                UpdateSubtitle(args.Text);

                if (WindowController.IsMainWindowVisible())
                    ScheduleDismiss(DismissAfterFinalInMainMode);
            });
            return Task.FromResult(false);
        });

        // STT 结束（超时/取消/无内容） → 延迟收起 Bubble
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnSttStopped, (_, _) =>
        {
            PostToUI(() => ScheduleDismiss(DismissAfterStopped));
            return Task.FromResult(false);
        });

        // TTS 开始播放 → 显示 Bubble（仅 Bubble 模式；主窗体可见时 AI 不走 TTS，气泡也不该出现）
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, (_, _) =>
        {
            if (WindowController.IsMainWindowVisible()) return Task.FromResult(false);
            PostToUI(ShowBubble);
            return Task.FromResult(false);
        });

        // TTS 字幕 → 更新当前播放的句子（仅 Bubble 模式）
        subscriber.Subscribe<VoiceTextArgs>(Events.OnTtsSubtitle, (_, args) =>
        {
            if (WindowController.IsMainWindowVisible()) return Task.FromResult(false);
            PostToUI(() =>
            {
                ShowBubble();
                UpdateSubtitle(args.Text);
            });
            return Task.FromResult(false);
        });

        // AI 推理完成 → 仅在主窗口可见时收起 Bubble
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiCompleted, (_, _) =>
        {
            PostToUI(() =>
            {
                if (WindowController.IsMainWindowVisible())
                    ScheduleDismiss(DismissAfterFinalInMainMode);
            });
            return Task.FromResult(false);
        });

        // 注意：不再订阅 OnMainWindowShown 强制 Dismiss。
        // 主窗体下气泡仍是用户的"语音监听反馈"，仅在 STT 结束/取消时由 OnSttStopped 收起。
    }

    // ──────────────────── 内部方法 ────────────────────

    /// <summary>
    /// 将操作调度到 UI 线程执行。
    /// </summary>
    private static void PostToUI(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
            }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>
    /// 显示 Bubble 并进入监听状态。
    /// 任何挂起的 Dismiss 都会被立刻取消，并恢复 Opacity，确保不会被旧的淡出抢关。
    /// </summary>
    private void ShowBubble()
    {
        // 任何 ShowBubble 调用都先把"挂起的关闭"撤销，避免被旧 Dismiss 关掉
        _dismissTimer?.Stop();

        Width = BubbleWidth;
        Height = BubbleHeight;
        Opacity = 1;

        if (_state == BubbleState.Hidden || !IsVisible)
        {
            // 首次显示：重置文案为"正在聆听"
            SubtitleText.Text = "正在聆听...";
            SubtitleText.Foreground = new SolidColorBrush(Color.FromRgb(170, 184, 232));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(86, 156, 214));
            RepositionAboveAnchor();
            Show();
        }
        else
        {
            // 已可见：仅刷新位置/状态点
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(86, 156, 214));
        }

        _state = BubbleState.Showing;
        StartHardTimeout();
    }

    /// <summary>
    /// 更新字幕文本。字数变多后自动滚到右端，气泡始终只看到最新识别出的内容。
    /// </summary>
    private void UpdateSubtitle(string text)
    {
        SubtitleText.Text = text;
        SubtitleText.Foreground = Brushes.White;
        ScrollSubtitleToEnd();
    }

    /// <summary>
    /// 将 ScrollViewer 滚到最右端，实现字幕"左退右进"。
    /// 需要等布局重新测量后设置 Offset，才能拿到最新的 Extent。
    /// </summary>
    private void ScrollSubtitleToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var scroll = SubtitleScroll;
                if (scroll is null) return;
                scroll.Offset = new Vector(scroll.Extent.Width, 0);
            }
            catch (ObjectDisposedException) { }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// 调度一次延迟关闭。重复调用会重置倒计时。
    /// 期间任何 ShowBubble 都会取消该 Timer。
    /// </summary>
    private void ScheduleDismiss(TimeSpan delay)
    {
        if (_state == BubbleState.Hidden) return;

        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 100, 100));

        _dismissTimer ??= new DispatcherTimer();
        _dismissTimer.Stop();
        _dismissTimer.Tick -= OnDismissTick;
        _dismissTimer.Tick += OnDismissTick;
        _dismissTimer.Interval = delay;
        _state = BubbleState.Dismissing;
        _dismissTimer.Start();
    }

    private void OnDismissTick(object? sender, System.EventArgs e)
    {
        _dismissTimer?.Stop();

        // 期间被新的 ShowBubble 抢回去了，则取消本次关闭
        if (_state != BubbleState.Dismissing) return;

        Hide();
        Opacity = 1;
        _state = BubbleState.Hidden;
        StopHardTimeout();
    }

    private void StartHardTimeout()
    {
        _hardTimeoutTimer ??= new DispatcherTimer();
        _hardTimeoutTimer.Stop();
        _hardTimeoutTimer.Tick -= OnHardTimeout;
        _hardTimeoutTimer.Tick += OnHardTimeout;
        _hardTimeoutTimer.Interval = HardTimeout;
        _hardTimeoutTimer.Start();
    }

    private void StopHardTimeout()
    {
        _hardTimeoutTimer?.Stop();
    }

    private void OnHardTimeout(object? sender, System.EventArgs e)
    {
        Logger.LogWarning("气泡硬超时触发，强制关闭。");
        _hardTimeoutTimer?.Stop();
        _dismissTimer?.Stop();
        Hide();
        Opacity = 1;
        _state = BubbleState.Hidden;
    }

    /// <summary>
    /// 锚点窗口移动时跟随重定位。
    /// </summary>
    internal void OnAnchorMoved()
    {
        if (!IsVisible || _anchorWindow is null) return;
        PostToUI(RepositionAboveAnchor);
    }

    /// <summary>
    /// 显示一条临时系统通知，用于主窗口隐藏时的连接状态提示。
    /// </summary>
    internal void ShowSystemNotification(string text, bool isConnected)
    {
        PostToUI(() =>
        {
            ShowBubble();
            UpdateSubtitle(text);
            StatusDot.Fill = new SolidColorBrush(
                isConnected ? Color.FromRgb(82, 196, 26) : Color.FromRgb(255, 120, 117));
            ScheduleDismiss(DismissAfterSystemNotification);
        });
    }

    /// <summary>
    /// 将气泡窗口定位到锚点窗口正上方。
    /// </summary>
    private void RepositionAboveAnchor()
    {
        if (_anchorWindow is null) return;

        // anchorPos / Position 都是物理像素 (PixelPoint)；
        // Width / Height 是 DIP，需要乘以 RenderScaling 才能与像素对齐，否则高 DPI 下会偏移。
        var anchorPos = _anchorWindow.Position;
        double scaleAnchor = _anchorWindow.RenderScaling;
        double scaleSelf = RenderScaling;

        int anchorWidthPx = (int)(_anchorWindow.Width * scaleAnchor);
        int selfWidthPx = (int)(Width * scaleSelf);
        int selfHeightPx = (int)(Height * scaleSelf);
        int gapPx = (int)(GapFromFloat * scaleAnchor);

        int x = anchorPos.X + (anchorWidthPx - selfWidthPx) / 2;
        int y = anchorPos.Y - selfHeightPx - gapPx;

        var screen = _anchorWindow.Screens.Primary;
        if (screen is not null)
        {
            var workArea = screen.WorkingArea;
            if (y < workArea.Y)
                y = workArea.Y;
        }

        Position = new PixelPoint(x, y);
    }
}