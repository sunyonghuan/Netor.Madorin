using Avalonia.Controls;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 子步骤卡片。
///
/// 视觉规范（09-界面交互设计.md §2.3 §3.6）：
/// - 折叠卡片标题 = 计划中定义的子步骤名（无 emoji）
/// - 内部串行容纳思维 / 工具 / 验收 FoldableCard
/// - 失败重试时：在原 _detailHost 内追加新卡片，不修改已有卡片
/// </summary>
public partial class SubStepCard : UserControl
{
    private readonly StackPanel _detailHost;

    public SubStepCard()
    {
        InitializeComponent();
        _detailHost = new StackPanel { Spacing = 4 };
        OuterCard.CardContent = _detailHost;
    }

    /// <summary>子步骤名（无 emoji）。</summary>
    public string CardTitle
    {
        get => OuterCard.CardTitle;
        set => OuterCard.CardTitle = value;
    }

    /// <summary>状态文字（"运行中" / "已完成" / "等待中" / "验收失败重试中"等）。</summary>
    public string? StatusText
    {
        get => OuterCard.StatusText;
        set => OuterCard.StatusText = value;
    }

    /// <summary>是否展开。运行中默认展开，完成后由外部主动折叠。</summary>
    public bool IsExpanded
    {
        get => OuterCard.IsExpanded;
        set => OuterCard.IsExpanded = value;
    }

    /// <summary>追加一张明细卡片（FoldableCard：思维/工具/验收）。</summary>
    public void AppendDetail(Control card)
    {
        _detailHost.Children.Add(card);
    }
}
