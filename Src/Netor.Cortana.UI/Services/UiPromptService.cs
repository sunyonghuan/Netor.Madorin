using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Netor.Cortana.UI;

/// <summary>
/// 原生 Avalonia 轻量提示辅助方法。
/// </summary>
public static class UiPromptService
{
    /// <summary>
    /// 显示一个原生 Avalonia 模态提示窗口。
    /// </summary>
    /// <param name="owner">用于查找宿主窗口和主题资源的当前控件。</param>
    /// <param name="title">提示窗口标题。</param>
    /// <param name="message">提示内容。</param>
    /// <returns>提示窗口关闭后完成的任务。</returns>
    public static async Task ShowDialogAsync(Control owner, string title, string message)
    {
        System.Diagnostics.Debug.WriteLine($"{title}: {message}");

        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is not Window window)
        {
            return;
        }

        Window? dialog = null;
        dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = FindBrush(owner, "MantleBrush", Color.FromRgb(30, 30, 46)),
            Content = BuildMessageDialogContent(owner, title, message, () => dialog?.Close())
        };

        await dialog.ShowDialog(window);
    }

    /// <summary>
    /// 在指定面板中临时插入一个 Toast，并在指定时间后自动移除。
    /// </summary>
    /// <param name="host">承载 Toast 的面板容器。</param>
    /// <param name="message">Toast 显示文本。</param>
    /// <param name="duration">显示时长；未指定时默认显示 2.2 秒。</param>
    /// <param name="horizontalAlignment">Toast 在容器中的水平对齐方式。</param>
    /// <returns>用于控制 Toast 生命周期的计时器。</returns>
    public static DispatcherTimer ShowToast(
        Panel host,
        string message,
        TimeSpan? duration = null,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Right)
    {
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 49, 50, 68)),
            BorderBrush = FindBrush(host, "BlueBrush", Color.FromRgb(137, 180, 250)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = horizontalAlignment,
            Child = new TextBlock
            {
                Text = message,
                Foreground = FindBrush(host, "TextBrush", Colors.White),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            }
        };

        host.Children.Add(toast);

        var timer = new DispatcherTimer
        {
            Interval = duration ?? TimeSpan.FromMilliseconds(2200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            host.Children.Remove(toast);
        };
        timer.Start();

        return timer;
    }

    /// <summary>
    /// 使用页面中预定义的 Border 和 TextBlock 显示内联 Toast。
    /// </summary>
    /// <param name="toastBorder">Toast 外层边框控件。</param>
    /// <param name="toastText">Toast 文本控件。</param>
    /// <param name="message">Toast 显示文本。</param>
    /// <param name="timer">当前 Toast 计时器引用；重复显示时会先停止旧计时器。</param>
    /// <param name="duration">显示时长；未指定时默认显示 2.4 秒。</param>
    public static void ShowInlineToast(
        Border toastBorder,
        TextBlock toastText,
        string message,
        ref DispatcherTimer? timer,
        TimeSpan? duration = null)
    {
        timer?.Stop();
        timer = null;

        toastText.Text = message;
        toastBorder.IsVisible = true;

        var newTimer = new DispatcherTimer
        {
            Interval = duration ?? TimeSpan.FromMilliseconds(2400)
        };
        newTimer.Tick += (_, _) =>
        {
            newTimer.Stop();
            toastBorder.IsVisible = false;
        };
        timer = newTimer;
        newTimer.Start();
    }

    /// <summary>
    /// 创建提示窗口内容区域。
    /// </summary>
    /// <param name="owner">用于读取主题资源的当前控件。</param>
    /// <param name="title">提示标题。</param>
    /// <param name="message">提示内容。</param>
    /// <param name="close">关闭窗口的回调。</param>
    /// <returns>可作为窗口内容的 Avalonia 控件。</returns>
    private static Control BuildMessageDialogContent(Control owner, string title, string message, Action close)
    {
        var closeButton = new Button
        {
            Content = "确定",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80
        };

        var stack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(18)
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = FindBrush(owner, "TextBrush", Colors.White),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = FindBrush(owner, "SubtextBrush", Color.FromRgb(186, 194, 222)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(closeButton);

        closeButton.Click += (_, _) => close();

        return new Border
        {
            Padding = new Thickness(0),
            Child = stack
        };
    }

    /// <summary>
    /// 从控件资源中查找画刷；找不到时使用指定颜色创建备用画刷。
    /// </summary>
    /// <param name="owner">用于查找资源的控件。</param>
    /// <param name="resourceKey">资源键。</param>
    /// <param name="fallback">资源不存在时使用的备用颜色。</param>
    /// <returns>主题资源画刷或备用实心画刷。</returns>
    private static IBrush FindBrush(Control owner, string resourceKey, Color fallback)
    {
        return owner.TryFindResource(resourceKey, out var resource) && resource is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
    }
}
