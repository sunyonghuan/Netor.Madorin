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

    /// <summary>
    /// 弹出一个二次确认对话框，返回用户选择（true = 确认 / false = 取消）。
    /// 适用场景：删除会话、重置数据等危险操作（界面重设计 B 步骤新增）。
    ///
    /// 与 <see cref="PromptAsync"/> 风格对称（同款 dark theme + CenterOwner）。
    /// 关键安全设计（决策"危险操作放最下"配套）：
    /// - 确认按钮用红色调（#6e2828）显式标识危险
    /// - 默认按钮 = 取消（IsDefault=true 在 cancelBtn），用户按 Enter 不会误删
    /// - ESC 同样关闭对话框 = 取消（IsCancel=true 在 cancelBtn）
    /// </summary>
    /// <param name="owner">父窗口（必填）。</param>
    /// <param name="title">对话框标题，如"删除会话"。</param>
    /// <param name="message">主要消息，如"确定要删除会话「xxx」吗？此操作不可撤销。"</param>
    /// <param name="confirmText">确认按钮文字，默认"删除"。</param>
    /// <param name="cancelText">取消按钮文字，默认"取消"。</param>
    /// <returns>true = 用户确认；false = 取消 / 关闭。</returns>
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "删除",
        string cancelText = "取消")
    {
        bool result = false;

        var messageBlock = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            FontSize = 13,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        // 危险按钮：红色调 + 不作为默认按钮（避免 Enter 误删）
        var confirmBtn = new Button
        {
            Content = confirmText,
            MinWidth = 80,
            Background = new SolidColorBrush(Color.FromRgb(0x6e, 0x28, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff)),
        };
        // 默认按钮 = 取消（防误删，呼应"危险操作放最下"决策）
        var cancelBtn = new Button
        {
            Content = cancelText,
            MinWidth = 80,
            IsCancel = true,
            IsDefault = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(confirmBtn);

        var stack = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(18),
        };
        stack.Children.Add(messageBlock);
        stack.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            Content = stack,
        };

        confirmBtn.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
