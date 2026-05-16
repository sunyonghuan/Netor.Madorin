using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Netor.Cortana.UI.Controls;

/// <summary>
/// 空状态用户控件（界面重设计 C1，决策 UI-5）。
/// 用于工作流 / 群聊模式"还没有任何任务"场景，引导用户新建。
///
/// 视觉规格：64×64 图标 + 18px 标题 + 13px 说明 + btn-link 主操作链接。
/// 详见 Docs/未来版本策划/界面重设计/02-空状态与文案.md §6。
///
/// C1 阶段：图标用 Emoji 顶替（避免引入图标资产同步问题）。
/// 后续阶段（C4+）若引入图标资产，把 IconText 改造为 IconSource (StreamGeometry / Bitmap)。
/// </summary>
public partial class EmptyState : UserControl
{
    /// <summary>
    /// 图标 emoji 字符（C1 阶段用 Emoji 顶替图标资产）。
    /// 推荐值：📋（工作流）/ 👥（群聊）/ 👈（提示选择）/ 🔍（搜索无结果）/ ⚠️（加载失败）/ 📁（文件夹）。
    /// 详见 02-空状态与文案.md §4 图标资产清单。
    /// </summary>
    public static readonly StyledProperty<string> IconTextProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(IconText), defaultValue: string.Empty);

    /// <summary>
    /// 图标 emoji 字符。
    /// </summary>
    public string IconText
    {
        get => GetValue(IconTextProperty);
        set => SetValue(IconTextProperty, value);
    }

    /// <summary>
    /// 标题文本（FontSize=18 SemiBold TextBrush）。
    /// </summary>
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Title), defaultValue: string.Empty);

    /// <summary>
    /// 标题文本。
    /// </summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// 说明文本（FontSize=13 SubtextBrush，最大宽度 380 自动换行）。
    /// </summary>
    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Description), defaultValue: string.Empty);

    /// <summary>
    /// 说明文本。多行可用 <c>&amp;#10;</c>（XML 实体的 LF 换行符）。
    /// </summary>
    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// 主链接文案（btn-link 蓝字，空字符串时整个按钮不显示）。
    /// </summary>
    public static readonly StyledProperty<string> PrimaryActionTextProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(PrimaryActionText), defaultValue: string.Empty);

    /// <summary>
    /// 主链接文案。
    /// </summary>
    public string PrimaryActionText
    {
        get => GetValue(PrimaryActionTextProperty);
        set => SetValue(PrimaryActionTextProperty, value);
    }

    /// <summary>
    /// 次链接文案（btn-link-secondary 灰字，空字符串时整个按钮不显示）。
    /// </summary>
    public static readonly StyledProperty<string> SecondaryActionTextProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(SecondaryActionText), defaultValue: string.Empty);

    /// <summary>
    /// 次链接文案。
    /// </summary>
    public string SecondaryActionText
    {
        get => GetValue(SecondaryActionTextProperty);
        set => SetValue(SecondaryActionTextProperty, value);
    }

    /// <summary>
    /// 主链接点击事件。
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> PrimaryActionClickEvent =
        RoutedEvent.Register<EmptyState, RoutedEventArgs>(nameof(PrimaryActionClick), RoutingStrategies.Bubble);

    /// <summary>
    /// 主链接点击事件。
    /// </summary>
    public event System.EventHandler<RoutedEventArgs>? PrimaryActionClick
    {
        add => AddHandler(PrimaryActionClickEvent, value);
        remove => RemoveHandler(PrimaryActionClickEvent, value);
    }

    /// <summary>
    /// 次链接点击事件。
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> SecondaryActionClickEvent =
        RoutedEvent.Register<EmptyState, RoutedEventArgs>(nameof(SecondaryActionClick), RoutingStrategies.Bubble);

    /// <summary>
    /// 次链接点击事件。
    /// </summary>
    public event System.EventHandler<RoutedEventArgs>? SecondaryActionClick
    {
        add => AddHandler(SecondaryActionClickEvent, value);
        remove => RemoveHandler(SecondaryActionClickEvent, value);
    }

    /// <summary>
    /// 初始化空状态控件。
    /// </summary>
    public EmptyState()
    {
        InitializeComponent();
        // 属性变化时同步 IsVisible（IconText / PrimaryActionText / SecondaryActionText 为空时隐藏对应元素）
        PropertyChanged += OnAvaloniaPropertyChanged;
    }

    /// <summary>
    /// 属性变化时同步元素可见性。
    /// </summary>
    private void OnAvaloniaPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IconTextProperty)
        {
            IconHost.IsVisible = !string.IsNullOrEmpty(IconText);
        }
        else if (e.Property == PrimaryActionTextProperty)
        {
            PrimaryActionButton.IsVisible = !string.IsNullOrEmpty(PrimaryActionText);
        }
        else if (e.Property == SecondaryActionTextProperty)
        {
            SecondaryActionButton.IsVisible = !string.IsNullOrEmpty(SecondaryActionText);
        }
    }

    /// <summary>
    /// 主链接 Click 路由：用 PrimaryActionClick 事件冒泡。
    /// </summary>
    private void OnPrimaryActionClick(object? sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(PrimaryActionClickEvent));
    }

    /// <summary>
    /// 次链接 Click 路由：用 SecondaryActionClick 事件冒泡。
    /// </summary>
    private void OnSecondaryActionClick(object? sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(SecondaryActionClickEvent));
    }
}
