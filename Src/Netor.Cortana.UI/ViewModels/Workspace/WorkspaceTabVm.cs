using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// 阶段 3B：工作台 Tab 的总 ViewModel。
///
/// 职责：
/// 1. 持有 <see cref="WorkflowTaskListVm"/>（列表）和 <see cref="TaskDetailVm"/>（详情）
/// 2. 监听 List.SelectedItem 变化 → 触发 Detail.LoadAsync(taskId)
/// 3. 暴露当前 WorkspaceId（用于 List 过滤）
///
/// 决策 8-A：启动时不自动选中任何任务（List.SelectedItem 默认 null）
/// 决策 12-A：单详情区共享（不支持多任务并行打开）
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §5.8。
/// </summary>
public sealed class WorkspaceTabVm : INotifyPropertyChanged
{
    public WorkspaceTabVm()
    {
        List = new WorkflowTaskListVm();
        Detail = new TaskDetailVm();

        // 监听列表选中切换 → 同步切换详情
        List.PropertyChanged += OnListPropertyChanged;
    }

    /// <summary>任务列表 VM。</summary>
    public WorkflowTaskListVm List { get; }

    /// <summary>任务详情 VM。</summary>
    public TaskDetailVm Detail { get; }

    /// <summary>
    /// Tab 切换时调用：刷新工作区 ID + 拉取列表（保证最新对齐）。
    /// 决策 8-A：不自动选中。
    /// </summary>
    public Task OnAttachedAsync(string workspaceId)
    {
        List.WorkspaceId = workspaceId ?? string.Empty;
        return List.LoadAsync();
    }

    private void OnListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowTaskListVm.SelectedItem)) return;

        var taskId = List.SelectedItem?.TaskId;
        _ = Detail.LoadAsync(taskId);
    }

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
