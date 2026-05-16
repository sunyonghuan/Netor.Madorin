using Avalonia.Controls;
using Avalonia.Interactivity;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.Workflow.Bridges;
using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// 工作流任务详情视图（界面重设计 C4，决策 UI-1 L2）。
///
/// 用作 MainWindow 主区视图（替代原 WorkspaceTab.axaml 整个 Col 2 + EmptyHint）。
/// 视觉规格 100% 迁移自原 WorkspaceTab.axaml 右半（line 236-475），事件处理器迁移
/// 自 WorkspaceTab.axaml.cs（line 113-204）。
///
/// DataContext 约定：本控件构造函数自行从 DI 解析 WorkspaceTabVm（Singleton），
/// 与 TaskListPanel 同源 — 用户在左侧点击列表项时，右侧主区详情自动联动（决策 DT-13）。
///
/// 唯一新增：EmptyState 主链接 "✨ 新建工作流任务"（OnEmptyStatePrimaryClick），
/// 等价于 TaskListPanel 顶部 "+ 新建任务" 按钮，方便用户在主区直接发起。
///
/// 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §4.3。
/// </summary>
public partial class WorkflowDetailView : UserControl
{
    private readonly WorkflowToChatBackflowService _backflowService;

    /// <summary>
    /// 当前控件持有的 VM 引用（构造时从 DI 解析；Singleton，与 TaskListPanel 同源）。
    /// </summary>
    private readonly WorkspaceTabVm _vm;

    public WorkflowDetailView()
    {
        InitializeComponent();
        _backflowService = App.Services.GetRequiredService<WorkflowToChatBackflowService>();
        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        DataContext = _vm;
    }

    // ──── 顶部操作 ────

    /// <summary>
    /// "取消任务" 按钮：调用 VM.Detail.CancelAsync。
    /// </summary>
    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            await _vm.Detail.CancelAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError($"取消任务失败：{ex.Message}");
        }
    }

    // ──── 阶段 5B：HITL 批准卡片操作 ────

    private async void OnApprovalApproveClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            var ok = await _vm.Detail.Approval.ApproveAsync(CancellationToken.None);
            if (!ok) ShowError("批准失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex)
        {
            ShowError($"批准失败：{ex.Message}");
        }
    }

    private async void OnApprovalReviseClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            if (string.IsNullOrWhiteSpace(_vm.Detail.Approval.RevisionInput))
            {
                ShowError("请先在文本框输入修改建议再点击[提交修改]");
                return;
            }
            var ok = await _vm.Detail.Approval.SubmitRevisionAsync(CancellationToken.None);
            if (!ok) ShowError("提交修改失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex)
        {
            ShowError($"提交修改失败：{ex.Message}");
        }
    }

    private async void OnApprovalRejectClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            var ok = await _vm.Detail.Approval.RejectAsync(CancellationToken.None);
            if (!ok) ShowError("取消任务失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex)
        {
            ShowError($"取消任务失败：{ex.Message}");
        }
    }

    // ──── 阶段 5B Phase 3：Workflow→Chat 回灌 ────

    /// <summary>
    /// "附加到对话" 按钮：把当前任务的最终报告作为助手消息追加到来源 Chat 会话。
    /// 一期实现：targetSessionId 为 null，让 service 回退到 task.SourceSessionId。
    /// </summary>
    private async void OnAttachToConversationClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var taskId = _vm.Detail.TaskId;
        if (string.IsNullOrEmpty(taskId))
        {
            ShowError("未选中任务");
            return;
        }

        try
        {
            var sessionId = await _backflowService.AttachToConversationAsync(
                taskId, targetSessionId: null, CancellationToken.None);
            ShowError($"已附加到会话 {sessionId[..Math.Min(8, sessionId.Length)]}…");
        }
        catch (InvalidOperationException ex)
        {
            ShowError($"附加失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            ShowError($"附加到对话失败：{ex.Message}");
        }
    }

    // ──── C4 新增：EmptyState 主链接（"✨ 新建工作流任务"） ────

    /// <summary>
    /// 空状态 "✨ 新建工作流任务" 链接：弹 NewTaskDialog（与 TaskListPanel 顶部按钮等价）。
    /// </summary>
    private async void OnEmptyStatePrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            var dialog = new Controls.NewTaskDialog
            {
                WorkspaceId = _vm.List.WorkspaceId,
            };

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

    /// <summary>
    /// 与原 WorkspaceTab 一致的简化错误提示（写 Debug 输出）。C5+ 接入 Snackbar 时替换。
    /// </summary>
    private void ShowError(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[WorkflowDetailView] {message}");
    }
}
