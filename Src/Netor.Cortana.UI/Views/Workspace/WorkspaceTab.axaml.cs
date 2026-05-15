using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.Workflow;
using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// 阶段 3B：工作台 Tab 顶层视图（MVVM 三层架构 [06] §5.8）。
///
/// code-behind 仅做 DI 与生命周期管理：
/// - 初始化 <see cref="WorkspaceTabVm"/> 作为 DataContext
/// - 转发列表点击事件 → VM.SelectedItem 切换
/// - 转发右键菜单 / 按钮事件 → IWorkflowExecutor 接口调用
///
/// 业务逻辑全部下沉到 ViewModel，UI 视觉用 DataTemplate 数据驱动渲染。
/// </summary>
public partial class WorkspaceTab : UserControl
{
    private readonly WorkspaceTabVm _vm;
    private readonly IWorkflowExecutor _executor;

    public WorkspaceTab()
    {
        InitializeComponent();
        _vm = new WorkspaceTabVm();
        _executor = App.Services.GetRequiredService<IWorkflowExecutor>();
        DataContext = _vm;
    }

    /// <summary>
    /// Tab 切换到工作台时由 MainWindow 调用：刷新工作区 + 拉取最新列表。
    /// </summary>
    public Task OnAttachedAsync(string workspaceId)
        => _vm.OnAttachedAsync(workspaceId);

    // ──── 列表项交互 ────

    private void OnTaskItemPressed(object? sender, PointerPressedEventArgs e)
    {
        // 仅左键选中，右键由 ContextMenu 处理
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (sender is Border { DataContext: WorkflowTaskItemVm item })
        {
            _vm.List.SelectedItem = item;
            UpdateActiveState(item.TaskId);
        }
    }

    /// <summary>
    /// 切换列表项选中样式（task-item-active）。
    /// 决策 [06] §5.8：纯数据驱动；这里仅是视觉高亮，不影响 VM 状态。
    /// </summary>
    private void UpdateActiveState(string activeTaskId)
    {
        // 找到 ItemsControl 内所有 Border.task-item，按 Tag 比对添加/移除 active 类
        if (TaskList.ItemsPanelRoot is null) return;
        foreach (var child in TaskList.ItemsPanelRoot.Children)
        {
            if (child is ContentPresenter cp && cp.Child is Border border)
            {
                var isActive = border.Tag is string id && id == activeTaskId;
                border.Classes.Set("task-item-active", isActive);
            }
        }
    }

    // ──── 顶部操作 ────

    private async void OnNewTaskClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Controls.NewTaskDialog
            {
                WorkspaceId = _vm.List.WorkspaceId,
            };

            // 模态对话框：在 MainWindow 中弹出
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await dialog.ShowDialog(owner);
            }
        }
        catch (Exception ex)
        {
            ShowError($"新建任务失败：{ex.Message}");
        }
    }

    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.Detail.CancelAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"取消任务失败：{ex.Message}");
        }
    }

    // ──── 右键菜单 ────

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        // 阶段 3B：先用简单 input dialog；阶段 4B+ 评估升级为 inline edit
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var newTitle = await InputBoxDialog.PromptAsync(owner, "重命名任务", "请输入新的标题：", "");
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        try
        {
            await _executor.RenameTitleAsync(taskId, newTitle.Trim(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"重命名失败：{ex.Message}");
        }
    }

    private async void OnTogglePinClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;
        var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;

        try
        {
            await _executor.SetPinnedAsync(taskId, !item.IsPinned, CancellationToken.None);
            item.IsPinned = !item.IsPinned;
        }
        catch (Exception ex)
        {
            ShowError($"置顶切换失败：{ex.Message}");
        }
    }

    private async void OnArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            await _executor.SetArchivedAsync(taskId, true, CancellationToken.None);
            // 归档后从列表移除
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
                if (item is not null) _vm.List.Items.Remove(item);
            });
        }
        catch (Exception ex)
        {
            ShowError($"归档失败：{ex.Message}");
        }
    }

    private async void OnDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            // 决策 10-A：基于源任务构造新请求 → 直接启动（简化版；4B+ 可加确认对话框）
            var template = await _executor.BuildRequestFromTemplateAsync(taskId, CancellationToken.None);
            await _executor.StartTaskAsync(template, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"复制任务失败：{ex.Message}");
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            await _executor.DeleteTaskAsync(taskId, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
                if (item is not null) _vm.List.Items.Remove(item);
                if (_vm.List.SelectedItem?.TaskId == taskId)
                {
                    _vm.List.SelectedItem = null;
                }
            });
        }
        catch (Exception ex)
        {
            ShowError($"删除失败：{ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        // 阶段 3B：日志输出 + 控制台；4B+ 加 Snackbar 提示
        System.Diagnostics.Debug.WriteLine($"[WorkspaceTab] {message}");
    }
}
