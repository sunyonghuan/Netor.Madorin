using Avalonia.Controls;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 完成总结卡片（阶段 4）。
/// 视觉规范：09-界面交互设计.md §2.4
/// </summary>
public partial class SummaryCard : UserControl
{
    public SummaryCard()
    {
        InitializeComponent();
    }

    /// <summary>总结正文（Markdown）。</summary>
    public string? SummaryMarkdown
    {
        get => SummaryRenderer.Markdown;
        set => SummaryRenderer.Markdown = value;
    }

    /// <summary>追加一张主步骤"归档"折叠卡片（默认折叠）。</summary>
    public void AppendStepArchive(FoldableCard archive)
    {
        archive.IsExpanded = false;
        archive.IndentLevel = 1;
        StepsArchiveHost.Children.Add(archive);
    }
}
