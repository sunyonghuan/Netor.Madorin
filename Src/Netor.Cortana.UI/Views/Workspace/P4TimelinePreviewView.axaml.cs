using Avalonia.Controls;
using Avalonia.Interactivity;

using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P4 时间线 UI 预览视图的 code-behind。
/// 纯预览用途，不依赖任何生产服务。
/// </summary>
public partial class P4TimelinePreviewView : UserControl
{
    public P4TimelinePreviewView()
    {
        InitializeComponent();
        DataContext = new P4TimelinePreviewVm();
    }

    /// <summary>切换计划概览面板展开/折叠。</summary>
    private void OnTogglePlanOverview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is P4TimelinePreviewVm vm)
            vm.IsPlanOverviewExpanded = !vm.IsPlanOverviewExpanded;
    }

    /// <summary>卡片内按钮点击（mock，仅 Debug 输出）。</summary>
    private void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionId)
        {
            System.Diagnostics.Debug.WriteLine($"[P4 Preview] Card action clicked: {actionId}");
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
