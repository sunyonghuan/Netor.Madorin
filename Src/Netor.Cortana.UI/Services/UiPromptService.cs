using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Netor.Cortana.UI;

/// <summary>
/// 原生 Avalonia 轻量提示辅助方法。
/// </summary>
public static class UiPromptService
{
    private const double DialogWidth = 380;
    private static readonly Color BaseColor = Color.FromRgb(30, 30, 30);
    private static readonly Color SurfaceColor = Color.FromRgb(37, 37, 38);
    private static readonly Color SurfaceBorderColor = Color.FromRgb(60, 60, 60);
    private static readonly Color TextColor = Color.FromRgb(204, 204, 204);
    private static readonly Color SubtextColor = Color.FromRgb(133, 133, 133);
    private static readonly Color AccentColor = Color.FromRgb(143, 175, 154);
    private static readonly Color AccentHoverColor = Color.FromRgb(163, 190, 140);
    private static readonly Color DangerColor = Color.FromRgb(241, 76, 76);
    private static readonly Color DangerHoverColor = Color.FromRgb(216, 58, 58);

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

        var dialog = CreateDialogWindow(owner, title, DialogWidth, new Panel());
        dialog.Content = BuildMessageDialogContent(owner, dialog, title, message);

        await dialog.ShowDialog(window);
    }

    /// <summary>
    /// 显示统一深色风格的确认窗口。
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(
        Control owner,
        string title,
        string message,
        string confirmText = "确定",
        bool isDanger = false,
        string cancelText = "取消")
    {
        var host = TopLevel.GetTopLevel(owner) as Window;
        if (host is null)
        {
            return false;
        }

        var confirmed = false;
        Window? dialog = null;

        var cancelButton = CreateButton(owner, cancelText, ButtonRole.Secondary);
        var confirmButton = CreateButton(owner, confirmText, isDanger ? ButtonRole.Danger : ButtonRole.Primary);

        cancelButton.Click += (_, _) => dialog?.Close();
        confirmButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog?.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);

        dialog = CreateDialogWindow(owner, title, DialogWidth, new Panel());

        var content = BuildDialogContent(
            owner,
            dialog,
            title,
            new TextBlock
            {
                Text = message,
                Foreground = FindBrush(owner, "TextBrush", TextColor),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            },
            buttons);

        dialog.Content = content;
        await dialog.ShowDialog(host);

        return confirmed;
    }

    /// <summary>
    /// 显示统一深色风格的输入窗口。
    /// </summary>
    public static async Task<string?> ShowInputAsync(
        Control owner,
        string title,
        string initialText,
        int selectionStart,
        int selectionEnd)
    {
        var host = TopLevel.GetTopLevel(owner) as Window;
        if (host is null)
        {
            return null;
        }

        string? result = null;
        Window? dialog = null;

        var inputBox = CreateTextBox(owner, initialText);
        inputBox.SelectionStart = Math.Clamp(selectionStart, 0, initialText.Length);
        inputBox.SelectionEnd = Math.Clamp(selectionEnd, inputBox.SelectionStart, initialText.Length);

        var cancelButton = CreateButton(owner, "取消", ButtonRole.Secondary);
        var confirmButton = CreateButton(owner, "确定", ButtonRole.Primary);

        cancelButton.Click += (_, _) => dialog?.Close();
        confirmButton.Click += (_, _) =>
        {
            result = inputBox.Text;
            dialog?.Close();
        };

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = inputBox.Text;
                dialog?.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                dialog?.Close();
                e.Handled = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(inputBox);

        dialog = CreateDialogWindow(owner, title, DialogWidth, new Panel());
        var content = BuildDialogContent(owner, dialog, title, body, buttons);
        dialog.Content = content;
        dialog.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                inputBox.Focus();
                inputBox.SelectionStart = Math.Clamp(selectionStart, 0, initialText.Length);
                inputBox.SelectionEnd = Math.Clamp(selectionEnd, inputBox.SelectionStart, initialText.Length);
            });
        };

        await dialog.ShowDialog(host);

        return result;
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
            Background = FindBrush(host, "Surface0Brush", SurfaceColor),
            BorderBrush = FindBrush(host, "Surface1Brush", SurfaceBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
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
    private static Control BuildMessageDialogContent(Control owner, Window dialog, string title, string message)
    {
        var closeButton = CreateButton(owner, "确定", ButtonRole.Primary);
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;

        var body = new TextBlock
        {
            Text = message,
            Foreground = FindBrush(owner, "TextBrush", TextColor),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };

        closeButton.Click += (_, _) => dialog.Close();

        return BuildDialogContent(owner, dialog, title, body, closeButton);
    }

    private static Window CreateDialogWindow(Control owner, string title, double width, Control content)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = FindBrush(owner, "BaseBrush", BaseColor),
            ShowInTaskbar = false,
            ExtendClientAreaToDecorationsHint = true,
            WindowDecorations = WindowDecorations.None,
            Content = content
        };
    }

    private static Control BuildDialogContent(Control owner, Window dialog, string title, Control body, Control footer)
    {
        return BuildDialogContent(owner, () => dialog, title, body, footer);
    }

    private static Control BuildDialogContent(Control owner, Func<Window?> getDialog, string title, Control body, Control footer)
    {
        var closeButton = CreateTitleButton(owner, "×");
        closeButton.Click += (_, _) => getDialog()?.Close();

        var titleBar = new Border
        {
            Height = 36,
            Background = FindBrush(owner, "BaseBrush", BaseColor),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(12, 0, 8, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = FindBrush(owner, "TextBrush", TextColor),
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    closeButton
                }
            }
        };
        Grid.SetColumn(closeButton, 1);

        titleBar.PointerPressed += (_, e) =>
        {
            var dialog = getDialog();
            if (dialog is not null && e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
            {
                dialog.BeginMoveDrag(e);
            }
        };

        var stack = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(18, 16, 18, 18)
        };
        stack.Children.Add(body);
        stack.Children.Add(footer);

        var root = new DockPanel
        {
            Background = FindBrush(owner, "BaseBrush", BaseColor)
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(new Border
        {
            BorderBrush = FindBrush(owner, "Surface1Brush", SurfaceBorderColor),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = stack
        });

        return new Border
        {
            Background = FindBrush(owner, "BaseBrush", BaseColor),
            BorderBrush = FindBrush(owner, "Surface1Brush", SurfaceBorderColor),
            BorderThickness = new Thickness(1),
            Child = root
        };
    }

    private static TextBox CreateTextBox(Control owner, string text)
    {
        return new TextBox
        {
            Text = text,
            Background = FindBrush(owner, "Surface0Brush", SurfaceColor),
            Foreground = FindBrush(owner, "TextBrush", TextColor),
            BorderBrush = FindBrush(owner, "Surface1Brush", SurfaceBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 6),
            FontSize = 13,
            MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CaretBrush = FindBrush(owner, "AccentBrush", AccentColor)
        };
    }

    private static Button CreateTitleButton(Control owner, string text)
    {
        var button = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            Foreground = FindBrush(owner, "SubtextBrush", SubtextColor),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            MinWidth = 26,
            MinHeight = 24,
            Padding = new Thickness(8, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        AddButtonHover(button, Brushes.Transparent, FindBrush(owner, "Surface0Brush", SurfaceColor), FindBrush(owner, "SubtextBrush", SubtextColor), FindBrush(owner, "TextBrush", TextColor));

        return button;
    }

    private static Button CreateButton(Control owner, string text, ButtonRole role)
    {
        IBrush background;
        IBrush hoverBackground;
        IBrush foreground;
        IBrush borderBrush;
        Thickness borderThickness;

        switch (role)
        {
            case ButtonRole.Primary:
                background = FindBrush(owner, "AccentBrush", AccentColor);
                hoverBackground = FindBrush(owner, "AccentHoverBrush", AccentHoverColor);
                foreground = FindBrush(owner, "CrustBrush", Color.FromRgb(17, 17, 17));
                borderBrush = Brushes.Transparent;
                borderThickness = new Thickness(0);
                break;
            case ButtonRole.Danger:
                background = Brushes.Transparent;
                hoverBackground = FindBrush(owner, "DangerHoverBrush", DangerHoverColor);
                foreground = FindBrush(owner, "DangerBrush", DangerColor);
                borderBrush = FindBrush(owner, "DangerBrush", DangerColor);
                borderThickness = new Thickness(1);
                break;
            default:
                background = FindBrush(owner, "Surface1Brush", SurfaceBorderColor);
                hoverBackground = FindBrush(owner, "Surface2Brush", Color.FromRgb(74, 74, 74));
                foreground = FindBrush(owner, "TextBrush", TextColor);
                borderBrush = Brushes.Transparent;
                borderThickness = new Thickness(0);
                break;
        }

        var hoverForeground = role == ButtonRole.Danger
            ? FindBrush(owner, "TextBrush", TextColor)
            : foreground;

        var button = new Button
        {
            Content = text,
            Background = background,
            Foreground = foreground,
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(14, 6),
            MinWidth = 78,
            MinHeight = 30,
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        AddButtonHover(button, background, hoverBackground, foreground, hoverForeground);

        return button;
    }

    private static void AddButtonHover(Button button, IBrush background, IBrush hoverBackground, IBrush foreground, IBrush hoverForeground)
    {
        button.PointerEntered += (_, _) =>
        {
            button.Background = hoverBackground;
            button.Foreground = hoverForeground;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = background;
            button.Foreground = foreground;
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

    private enum ButtonRole
    {
        Secondary,
        Primary,
        Danger
    }
}
