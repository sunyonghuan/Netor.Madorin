using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views;

/// <summary>
/// 圆形浮动窗口 — 桌面悬浮球。
/// 纯 XAML 实现，无 WebView2，无 Chromium Renderer 进程开销。
/// </summary>
public partial class FloatWindow : Window
{
    private const int FloatSize = 140;

    /// <summary>语音互动结束后保留 active 状态多久（避免事件间隙闪烁）。</summary>
    private static readonly TimeSpan DeactivateDelay = TimeSpan.FromMilliseconds(900);

    private DispatcherTimer? _deactivateTimer;

    /// <summary>
    /// 窗口位置发生变化时触发，供气泡窗口跟随移动。
    /// </summary>
    internal event Action? FloatPositionChanged;

    public FloatWindow()
    {
        InitializeComponent();
        FloatLogoImage.Source = LoadLogoBitmap();

        // 定位到屏幕右下角（留出 120px 边距）
        var screen = Screens.Primary;
        if (screen is not null)
        {
            const int margin = 120;
            var workArea = screen.WorkingArea;
            Position = new PixelPoint(
                workArea.Right - FloatSize - margin,
                workArea.Bottom - FloatSize - margin);
        }

        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;

        // 监听 Window 基类的 PositionChanged 事件
        base.PositionChanged += (_, _) => FloatPositionChanged?.Invoke();

        SubscribeVoiceEvents();
    }

    /// <summary>
    /// 从品牌默认入口加载悬浮球 Logo。
    /// </summary>
    private static Bitmap LoadLogoBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.LogoImageFileName)));
        return new Bitmap(stream);
    }

    // ──────── 拖拽移动（使用操作系统原生拖拽） ────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ──────── 双击唤起主窗口 ────────

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ShowMainWindow();
    }

    /// <summary>
    /// 显示主窗口。
    /// </summary>
    internal void ShowMainWindow()
    {
        var controller = App.Services.GetRequiredService<IWindowController>();
        controller.ShowMainWindow();
    }

    // ──────── 语音状态驱动光晕 ────────

    /// <summary>
    /// 订阅语音流程事件，驱动悬浮球进入/退出 Active 高频脉冲状态。
    /// </summary>
    private void SubscribeVoiceEvents()
    {
        var subscriber = App.Services.GetRequiredService<ISubscriber>();

        // 进入 Active：唤醒、STT 有结果、TTS 播报中
        subscriber.On(Events.OnWakeWordDetected, (_, _) =>
        {
            SetActive(true);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, (_, _) =>
        {
            SetActive(true);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, _) =>
        {
            SetActive(true);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, (_, _) =>
        {
            SetActive(true);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceTextArgs>(Events.OnTtsSubtitle, (_, _) =>
        {
            SetActive(true);
            return Task.FromResult(false);
        });

        // 退出 Active（延迟 ~1s 防抖，避免 STT 结束→TTS 开始之间的瞬间断档）
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnSttStopped, (_, _) =>
        {
            ScheduleDeactivate();
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, _) =>
        {
            ScheduleDeactivate();
            return Task.FromResult(false);
        });
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnChatCompleted, (_, _) =>
        {
            ScheduleDeactivate();
            return Task.FromResult(false);
        });
    }

    private void SetActive(bool on)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _deactivateTimer?.Stop();
                bool already = FloatRoot.Classes.Contains("active");
                if (on && !already) FloatRoot.Classes.Add("active");
                else if (!on && already) FloatRoot.Classes.Remove("active");
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void ScheduleDeactivate()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _deactivateTimer ??= new DispatcherTimer { Interval = DeactivateDelay };
                _deactivateTimer.Stop();
                _deactivateTimer.Tick -= OnDeactivateTick;
                _deactivateTimer.Tick += OnDeactivateTick;
                _deactivateTimer.Start();
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void OnDeactivateTick(object? sender, System.EventArgs e)
    {
        _deactivateTimer?.Stop();
        FloatRoot.Classes.Remove("active");
    }
}
