using Avalonia.Controls;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式用户气泡（无 MaxWidth、铺满、内嵌 Markdown 渲染）。
/// 视觉规范：09-界面交互设计.md §3.2
/// </summary>
public partial class UserBubble : UserControl
{
    public UserBubble()
    {
        InitializeComponent();
    }

    /// <summary>用户输入文本（Markdown 渲染）。</summary>
    public string? Text
    {
        get => ContentRenderer.Markdown;
        set => ContentRenderer.Markdown = value;
    }
}
