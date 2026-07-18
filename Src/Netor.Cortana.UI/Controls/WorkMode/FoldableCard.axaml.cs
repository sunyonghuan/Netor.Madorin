using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式通用折叠卡片。
///
/// 视觉规范（来源：09-界面交互设计.md §3.4）：
/// - 箭头图片置于标题左侧（折叠时朝右，展开时旋转 90° 朝下）
/// - 标题文本无 emoji
/// - 内容区缩进对齐到标题文字
/// - 整个 Header 可点击切换展开/折叠
///
/// 设计原则：与 RealtimeProcessCard 保持一致——不用 Binding，code-behind 直接更新 UI 字段，AOT 安全。
/// </summary>
public partial class FoldableCard : UserControl
{
    private bool _isExpanded = false;
    private string _title = string.Empty;
    private string? _status;
    private int _indentLevel;
    private object? _content;

    public FoldableCard()
    {
        InitializeComponent();
        UpdateArrow();
        UpdateIndent();
        UpdateExpanded();
    }

    /// <summary>标题文本（禁止 emoji）。</summary>
    public string CardTitle
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            TitleBlock.Text = _title;
        }
    }

    /// <summary>右侧状态文字（如"运行中"/"已完成"/"等待中"）。空字符串则隐藏。</summary>
    public string? StatusText
    {
        get => _status;
        set
        {
            _status = value;
            var show = !string.IsNullOrWhiteSpace(value);
            StatusBlock.Text = value ?? string.Empty;
            StatusBlock.IsVisible = show;
        }
    }

    /// <summary>是否展开。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            UpdateExpanded();
            UpdateArrow();
        }
    }

    /// <summary>
    /// 缩进级别。
    /// 保留给调用侧表达层级，但卡片自身始终铺满父容器宽度。
    /// </summary>
    public int IndentLevel
    {
        get => _indentLevel;
        set
        {
            _indentLevel = value;
            UpdateIndent();
        }
    }

    /// <summary>内容区控件。</summary>
    public object? CardContent
    {
        get => _content;
        set
        {
            _content = value;
            ContentArea.Content = value;
        }
    }

    private void UpdateArrow()
    {
        if (ArrowImage.RenderTransform is RotateTransform rt)
            rt.Angle = _isExpanded ? 90 : 0;
    }

    private void UpdateIndent()
    {
        RootBorder.Margin = new Thickness(0);
    }

    private void UpdateExpanded()
    {
        ContentArea.IsVisible = _isExpanded;
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        IsExpanded = !_isExpanded;
    }
}
