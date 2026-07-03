using Avalonia.Controls;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式 AI 平铺内容（无 Border / 无背景 / 居中铺满）。
/// 视觉规范：09-界面交互设计.md §3.3
/// 支持在主体 Markdown 之前追加任意数量的折叠卡片（思维/工具/验收）。
/// </summary>
public partial class AiContent : UserControl
{
    public AiContent()
    {
        InitializeComponent();
    }

    /// <summary>主体 Markdown 内容。</summary>
    public string? Markdown
    {
        get => ContentRenderer.Markdown;
        set => ContentRenderer.Markdown = value;
    }

    /// <summary>追加一张前置卡片（位于 Markdown 主体之前）。</summary>
    public void AddPrependedCard(Control card)
    {
        PrependedCardsHost.Children.Add(card);
    }
}
