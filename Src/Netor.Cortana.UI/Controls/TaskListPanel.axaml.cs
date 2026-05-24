using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.Cortana.UI.Views.Workspace;

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
    private readonly TaskExecutionEngine _engine;

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
    /// 初始化。从 DI 解析 TaskExecutionEngine + WorkspaceTabVm，并自行设置 DataContext。
    /// </summary>
    public TaskListPanel()
    {
        InitializeComponent();
        _engine = App.Services.GetRequiredService<TaskExecutionEngine>();
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
    /// "+ 新建任务" 按钮。P4：聚焦到 WorkflowDetailView 的输入框（新建任务通过输入框发起）。
    /// </summary>
    private void OnNewTaskClick(object? sender, RoutedEventArgs e)
    {
        _logger.LogInformation("[TaskListPanel] OnNewTaskClick: WorkspaceId={WorkspaceId}, SubModeFilter={SubModes}",
            _vm.List.WorkspaceId, _vm.List.SubModeFilter is null ? "(null)" : string.Join(",", _vm.List.SubModeFilter));

        // P4: 新建任务通过右侧 WorkflowDetailView 输入框发起，这里聚焦到输入框
        // 查找当前视觉树中的 WorkflowDetailView 并聚焦其 InputBox
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            var inputBox = window.FindControl<TextBox>("InputBox");
            if (inputBox is not null)
            {
                inputBox.Focus();
                return;
            }
        }

        _logger.LogDebug("[TaskListPanel] 未找到 InputBox，可能尚未加载");
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
            var success = await _engine.RenameTaskAsync(taskId, newTitle.Trim(), CancellationToken.None);
            if (success)
            {
                _logger.LogInformation("[TaskListPanel] 任务已重命名: {TaskId} → {Title}", taskId, newTitle.Trim());
                // 更新 UI 中的标题
                var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
                if (item is not null)
                {
                    item.Title = newTitle.Trim();
                }
            }
            else
            {
                ShowError($"重命名失败：任务不存在 (TaskId={taskId})");
            }
        }
        catch (Exception ex)
        {
            ShowError($"重命名失败：{ex.Message}", ex);
        }
    }

    private void OnTogglePinClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not MenuItem { Tag: string taskId }) return;
        var item = _vm.List.Items.FirstOrDefault(x => x.TaskId == taskId);
        if (item is null) return;

        try
        {
            // TODO P4: TaskExecutionEngine 尚未实现 SetPinnedAsync，待 P4-任务元数据管理 补充
            // await _engine.SetPinnedAsync(taskId, !item.IsPinned, CancellationToken.None);
            _logger.LogWarning("[TaskListPanel] SetPinnedAsync 尚未实现 (TaskId={TaskId})", taskId);
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
            // TODO P4: TaskExecutionEngine 尚未实现 SetArchivedAsync，待 P4-任务元数据管理 补充
            // await _engine.SetArchivedAsync(taskId, true, CancellationToken.None);
            _logger.LogWarning("[TaskListPanel] SetArchivedAsync 尚未实现 (TaskId={TaskId})", taskId);
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

    private void OnDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string taskId }) return;

        try
        {
            // TODO P4: TaskExecutionEngine 尚未实现 BuildRequestFromTemplateAsync，待 P4-模板复制 补充
            // 原逻辑：var template = await _engine.BuildRequestFromTemplateAsync(taskId, CancellationToken.None);
            //         await _engine.StartTaskAsync(template, CancellationToken.None);
            _logger.LogWarning("[TaskListPanel] BuildRequestFromTemplateAsync / 复制任务尚未实现 (TaskId={TaskId})", taskId);
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
            var success = await _engine.DeleteTaskAsync(taskId, CancellationToken.None);
            if (success)
            {
                _logger.LogInformation("[TaskListPanel] 任务已删除: {TaskId}", taskId);
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
            else
            {
                ShowError($"删除失败：任务不存在或正在运行中 (TaskId={taskId})");
            }
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
