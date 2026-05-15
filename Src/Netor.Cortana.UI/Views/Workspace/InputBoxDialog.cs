using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// 阶段 3B：极简文本输入对话框。用于重命名、文本输入等场景。
/// 不绑定 ViewModel，仅作为简化版替代品；4B+ 可评估升级为 inline-edit 模式。
/// </summary>
internal static class InputBoxDialog
{
    /// <summary>
    /// 弹出一个文本输入对话框，返回用户输入。点击取消或关闭返回 null。
    /// </summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string defaultValue)
    {
        string? result = null;

        var input = new TextBox
        {
            Text = defaultValue,
            PlaceholderText = "请输入…",
            Padding = new Avalonia.Thickness(10, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3c, 0x3c, 0x3c)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(3),
        };

        var okBtn = new Button { Content = "确定", MinWidth = 80, IsDefault = true };
        var cancelBtn = new Button { Content = "取消", MinWidth = 80, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            FontSize = 13,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(18),
        };
        stack.Children.Add(labelBlock);
        stack.Children.Add(input);
        stack.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            Content = stack,
        };

        okBtn.Click += (_, _) =>
        {
            result = input.Text ?? string.Empty;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) =>
        {
            result = null;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
