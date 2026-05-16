using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Workflow;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.Cortana.UI.Views.Workspace;
using Netor.Cortana.UI.Views.Workspace.Controls;

namespace Netor.Cortana.UI.Controls;

/// <summary>
/// 任务列表面板（界面重设计 C4，决策 UI-1 L2 + UI-9）。
///
/// 用作左侧 LeftPanel.Tab2 的内容（替代 C3 阶段的 EmptyState 占位）。
/// 视觉规格 100% 迁移自原 WorkspaceTab.axaml 左半（Col 0），事件处理器迁移
/// 自 WorkspaceTab.axaml.cs（line 47-111 + 207-301）。
///
/// DataContext 约定：本控件构造函数自行从 DI 解析 WorkspaceTabVm（Singleton），
/// 不依赖父容器注入。这样 LeftPanel.axaml 可以保留 LeftPanelVm 作为自身 DataContext，
/// 而 TaskListPanel 内部 binding 走 WorkspaceTabVm — 父子解耦清晰（决策 DT-13）。
///
/// 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §4.2。
/// </summary>
public partial class TaskListPanel : UserControl
{
    private readonly IWorkflowExecutor _executor;

    /// <summary>
    /// 当前控件持有的 VM 引用（构造时从 DI 解析；Singleton，与 WorkflowDetailView 同源）。
    /// </summary>
    private readonly WorkspaceTabVm _vm;

    /// <summary>
    /// Bug 诊断（2026-05-16 用户反馈"群聊 tab 新建任务无响应"）：
    /// 原 <see cref="ShowError"/> 走 Debug.WriteLine 在 Release/AOT 下被编译器剥离，
    /// 异常被静默吞掉。改用 ILogger 让所有错误在生产日志可见。
    /// </summary>
    private readonly ILogger<TaskListPanel> _logger;

    /// <summary>
    /// 初始化。从 DI 解析 IWorkflowExecutor + WorkspaceTabVm，并自行设置 DataContext。
    /// </summary>
    public TaskListPanel()
    {
        InitializeComponent();
        _executor = App.Services.GetRequiredService<IWorkflowExecutor>();
        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        _logger = App.Services.GetRequiredService<ILogger<TaskListPanel>>();
        DataContext = _vm;
    }

    // ──── 列表项交互 ────

    /// <summary>
    /// 列表项左键点击：选中任务。右键由 ContextMenu 处理。
    /// </summary>
    private void OnTaskItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (sender is Border { DataContext: WorkflowTaskItemVm item })
        {
            _vm.List.SelectedItem = item;
            UpdateActiveState(item.TaskId);
        }
    }

    /// <summary>
    /// 切换列表项选中样式（task-item-active）。
    /// </summary>
    private void UpdateActiveState(string activeTaskId)
    {
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

    /// <summary>
    /// "+ 新建任务" 按钮：弹出 NewTaskDialog 模态框。
    /// </summary>
    private async void OnNewTaskClick(object? sender, RoutedEventArgs e)
    {
        // Bug 诊断：入口日志确认按钮事件触发（Release/AOT 模式下 Debug.WriteLine 不可见）
        _logger.LogInformation("[TaskListPanel] OnNewTaskClick: WorkspaceId={WorkspaceId}, SubModeFilter={SubModes}",
            _vm.List.WorkspaceId, _vm.List.SubModeFilter is null ? "(null)" : string.Join(",", _vm.List.SubModeFilter));

        if (_vm is null) return;

        try
        {
            var dialog = new NewTaskDialog
            {
                WorkspaceId = _vm.List.WorkspaceId,
            };

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                _logger.LogInformation("[TaskListPanel] Showing NewTaskDialog with owner={OwnerType}", owner.GetType().Name);
                await dialog.ShowDialog(owner);
                _logger.LogInformation("[TaskListPanel] NewTaskDialog closed, CreatedTaskId={TaskId}",
                    dialog.CreatedTaskId ?? "(null)");
            }
            else
            {
                _logger.LogWarning("[TaskListPanel] TopLevel.GetTopLevel(this) returned null — dialog NOT shown");
            }
        }
        catch (Exception ex)
        {
            ShowError($"新建任务失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 搜索框文字变化（决策 6-3-A 子串 LIKE 匹配 + 200ms 防抖，由 VM.List.ApplySearch 实现）。
    /// </summary>
    private void OnTaskSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not TextBox tb) return;
        _vm.List.ApplySearch(tb.Text);
    }

    // ──── 右键菜单 ────

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var newTitle = await InputBoxDialog.PromptAsync(owner, "重命名任务", "请输入新的标题：", "");
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        try
        {
            await _executor.RenameTitleAsync(taskId, newTitle.Trim(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"重命名失败：{ex.Message}", ex);
        }
    }

    private async void OnTogglePinClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
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
            ShowError($"置顶切换失败：{ex.Message}", ex);
        }
    }

    private async void OnArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            await _executor.SetArchivedAsync(taskId, true, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
                if (item is not null) _vm.List.Items.Remove(item);
            });
        }
        catch (Exception ex)
        {
            ShowError($"归档失败：{ex.Message}", ex);
        }
    }

    private async void OnDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            // 决策 10-A：基于源任务构造新请求 → 直接启动（与原 WorkspaceTab 行为一致）。
            var template = await _executor.BuildRequestFromTemplateAsync(taskId, CancellationToken.None);
            await _executor.StartTaskAsync(template, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"复制任务失败：{ex.Message}", ex);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
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
            ShowError($"删除失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// Bug 诊断版（2026-05-16）：原版用 Debug.WriteLine 在 Release/AOT 下被剥离，异常静默吞掉。
    /// 改为 ILogger.LogError + Console.Error.WriteLine 让所有错误在生产日志可见。
    /// C5+ 接入 Snackbar 时把第二个调用替换为 UI 弹窗。
    /// </summary>
    private void ShowError(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            _logger.LogError("[TaskListPanel] {Message}", message);
            Console.Error.WriteLine($"[TaskListPanel] {message}");
        }
        else
        {
            _logger.LogError(ex, "[TaskListPanel] {Message}", message);
            Console.Error.WriteLine($"[TaskListPanel] {message}\n{ex}");
        }
    }
}
