using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 时间线（TimelineBlock）。
///
/// 视觉规范（09-界面交互设计.md §3.6）：
/// - 作为单一控件插入消息流的一项，执行完成后整块"凝固"
/// - 主步骤渐次"点亮"（AddMainStep）
/// - 子步骤通过 SubStepCard 追加到当前主步骤
/// - 圆点统一灰色，不分状态颜色
/// - 状态通过主步骤标题行右侧文字传达
/// </summary>
public partial class TimelineBlock : UserControl
{
    private static readonly IBrush DotFill = new SolidColorBrush(Color.Parse("#5A5A5A"));
    private static readonly IBrush DotStroke = new SolidColorBrush(Color.Parse("#3c3c3c"));
    private static readonly IBrush LineFill = new SolidColorBrush(Color.Parse("#3c3c3c"));

    private readonly List<TimelineStepHandle> _steps = new();

    public TimelineBlock()
    {
        InitializeComponent();
    }

    /// <summary>当前主步骤数量。</summary>
    public int StepCount => _steps.Count;

    /// <summary>追加一个主步骤（圆点 + 折叠卡片）。返回操作句柄用于追加子步骤、改状态。</summary>
    public TimelineStepHandle AddMainStep(string title, string? statusText = "等待中", bool expanded = true)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*"),
            RowDefinitions = new RowDefinitions("Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 0),
        };

        // 左列：圆点 + 竖线
        var leftStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 28,
        };

        // 竖线（连接到下一个圆点；最后一个步骤的竖线在外部隐藏）
        var line = new Rectangle
        {
            Width = 2,
            Fill = LineFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = DotFill,
            Stroke = DotStroke,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
        };

        leftStack.Children.Add(line);
        leftStack.Children.Add(dot);
        Grid.SetColumn(leftStack, 0);

        // 右列：主步骤标题 + 子步骤卡片列表。主步骤本身不再占用卡片层级。
        var detailHost = new StackPanel
        {
            Spacing = 4,
            IsVisible = expanded,
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var statusBlock = new TextBlock
        {
            Text = statusText ?? string.Empty,
            FontSize = 11,
            Opacity = 0.65,
            Foreground = new SolidColorBrush(Color.Parse("#7A7A7A")),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = !string.IsNullOrWhiteSpace(statusText),
        };

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 4),
        };
        titleRow.Children.Add(titleBlock);
        Grid.SetColumn(statusBlock, 1);
        titleRow.Children.Add(statusBlock);

        var rightStack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        rightStack.Children.Add(titleRow);
        rightStack.Children.Add(detailHost);
        Grid.SetColumn(rightStack, 1);

        grid.Children.Add(leftStack);
        grid.Children.Add(rightStack);

        // 步骤之间的间距（上下 6px）
        grid.Margin = new Thickness(0, _steps.Count == 0 ? 0 : 6, 0, 0);

        StepsHost.Children.Add(grid);

        // 隐藏上一个主步骤的"末尾留白"——其实更简单：每个步骤的竖线都向上延伸到下一个圆点，
        // 最后一个步骤添加后，把它自己的 line 隐藏即可。
        if (_steps.Count > 0)
        {
            // 之前的最后一步现在不是最后了，恢复其竖线显示
            _steps[^1].Line.IsVisible = true;
        }

        var handle = new TimelineStepHandle(statusBlock, detailHost, dot, line);

        // 当前作为最后一个，隐藏其竖线（避免末尾长尾巴）
        line.IsVisible = false;

        _steps.Add(handle);
        return handle;
    }

    /// <summary>清空所有主步骤（开新任务时调用）。</summary>
    public void Clear()
    {
        _steps.Clear();
        StepsHost.Children.Clear();
    }
}

/// <summary>
/// 主步骤操作句柄。
/// </summary>
public sealed class TimelineStepHandle
{
    public Ellipse Dot { get; }
    public Rectangle Line { get; }
    private readonly TextBlock _statusBlock;
    private readonly StackPanel _detailHost;

    internal TimelineStepHandle(TextBlock statusBlock, StackPanel detailHost, Ellipse dot, Rectangle line)
    {
        _statusBlock = statusBlock;
        _detailHost = detailHost;
        Dot = dot;
        Line = line;
    }

    /// <summary>追加一张子步骤卡片到当前主步骤。</summary>
    public void AppendSubStep(SubStepCard subStep)
    {
        _detailHost.Children.Add(subStep);
    }

    /// <summary>更新主步骤状态文字。</summary>
    public void SetStatus(string? statusText)
    {
        _statusBlock.Text = statusText ?? string.Empty;
        _statusBlock.IsVisible = !string.IsNullOrWhiteSpace(statusText);
    }

    /// <summary>展开/折叠主步骤（完成后通常折叠）。</summary>
    public void SetExpanded(bool expanded)
    {
        _detailHost.IsVisible = expanded;
    }
}
