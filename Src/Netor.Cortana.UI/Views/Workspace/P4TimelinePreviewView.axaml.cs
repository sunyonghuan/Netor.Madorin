using Avalonia.Controls;
using Avalonia.Interactivity;

using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P4 时间线 UI 视图的 code-behind。
/// 支持两种 DataContext：
/// - <see cref="P4TimelinePreviewVm"/>：mock 数据预览（设计验证）
/// - <see cref="P4TaskDetailVm"/>：实时事件驱动（生产模式）
/// </summary>
public partial class P4TimelinePreviewView : UserControl
{
    /// <summary>
    /// 默认构造函数：使用 mock 数据（设计预览入口）。
    /// </summary>
    public P4TimelinePreviewView()
    {
        InitializeComponent();
        DataContext = new P4TimelinePreviewVm();
    }

    /// <summary>
    /// 带参构造函数：使用实时 ViewModel（P4 任务详情入口）。
    /// </summary>
    public P4TimelinePreviewView(P4TaskDetailVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>切换计划概览面板展开/折叠。</summary>
    private void OnTogglePlanOverview(object? sender, RoutedEventArgs e)
    {
        switch (DataContext)
        {
            case P4TimelinePreviewVm mockVm:
                mockVm.IsPlanOverviewExpanded = !mockVm.IsPlanOverviewExpanded;
                break;
            case P4TaskDetailVm liveVm:
                liveVm.IsPlanOverviewExpanded = !liveVm.IsPlanOverviewExpanded;
                break;
        }
    }

    /// <summary>卡片内按钮点击。</summary>
    private void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionId)
        {
            System.Diagnostics.Debug.WriteLine($"[P4 Timeline] Card action clicked: {actionId}");
        }
    }

    /// <summary>关闭预览，恢复原始内容。</summary>
    private void OnClosePreview(object? sender, RoutedEventArgs e)
    {
        // 从父 Panel 中移除自己，让 EmptyState 重新可见
        if (Parent is Panel panel)
        {
            panel.Children.Remove(this);
        }
    }
}
