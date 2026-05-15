using Avalonia.Controls;
using Avalonia.Interactivity;

using Netor.Cortana.Entitys;
using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace.Controls;

/// <summary>
/// 阶段 3B：新建 Workflow 任务对话框。
///
/// 视图与 <see cref="NewTaskDialogVm"/> 强绑定（x:DataType），所有表单字段双向绑定。
/// code-behind 仅做生命周期 + 提交事件转发。
/// </summary>
public partial class NewTaskDialog : Window
{
    private readonly NewTaskDialogVm _vm;

    public NewTaskDialog()
    {
        InitializeComponent();
        _vm = new NewTaskDialogVm();
        DataContext = _vm;
    }

    /// <summary>对话框启动前由调用方注入工作区 ID。</summary>
    public string WorkspaceId
    {
        get => _vm.WorkspaceId;
        set => _vm.WorkspaceId = value;
    }

    /// <summary>提交成功后返回的 taskId（关闭后调用方读取）。</summary>
    public string? CreatedTaskId { get; private set; }

    // ──── 子模式切换 ────

    private void OnSubModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.SelectedItem is ComboBoxItem { Tag: string subMode })
        {
            _vm.SubMode = subMode;
        }
    }

    // ──── 成员选择 ────

    private void OnMemberCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AgentEntity agent } cb) return;

        if (cb.IsChecked == true)
        {
            if (!_vm.SelectedMembers.Any(m => m.Id == agent.Id))
                _vm.SelectedMembers.Add(agent);
        }
        else
        {
            var existing = _vm.SelectedMembers.FirstOrDefault(m => m.Id == agent.Id);
            if (existing is not null)
                _vm.SelectedMembers.Remove(existing);
        }
        // ObservableCollection 的 CollectionChanged 已由 VM 自身映射到 CanSubmit 的 PropertyChanged，
        // 这里不需要再手动触发。保留显式调用作为兜底，避免某些边缘情况下绑定不刷新。
        _vm.NotifyCanSubmitChanged();
    }

    // ──── 底部按钮 ────

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var taskId = await _vm.SubmitAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(taskId))
            {
                CreatedTaskId = taskId;
                Close();
            }
        }
        catch (Exception ex)
        {
            // 错误已由 VM 收集到 ValidationError，UI 已展示；本地仅记录
            System.Diagnostics.Debug.WriteLine($"[NewTaskDialog] Submit exception: {ex.Message}");
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CreatedTaskId = null;
        Close();
    }
}
