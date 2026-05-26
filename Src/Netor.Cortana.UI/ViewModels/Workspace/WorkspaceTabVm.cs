using Avalonia.Threading;

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
        Detail = new P4TaskDetailVm();

        // 监听列表选中切换 → 同步切换详情
        List.PropertyChanged += OnListPropertyChanged;
    }

    /// <summary>任务列表 VM。</summary>
    public WorkflowTaskListVm List { get; }

    /// <summary>任务详情 VM（P4 过渡：替代已删除的 TaskDetailVm）。</summary>
    public P4TaskDetailVm Detail { get; }

    /// <summary>
    /// Tab 切换时调用：刷新工作区 ID + SubMode 过滤 + 拉取列表（保证最新对齐）。
    /// 决策 8-A：不自动选中。
    /// P1 群聊真实化（收尾决策 DT-9，2026-05-16 落地）：subModes 参数区分「工作流」/「群聊」tab。
    /// - 「工作流」tab 传 ["magentic", "parallelanalysis"]
    /// - 「群聊」tab 传 ["groupchat"]
    /// - null 或空集合 = 不过滤（兼容旧调用 / 未指定模式的场景）
    /// 详见 Docs/未来版本策划/界面重设计/05-阶段总结.md §3.1 + §6.2。
    /// </summary>
    public Task OnAttachedAsync(string workspaceId, IReadOnlyList<string>? subModes = null)
    {
        // 切换 Tab 时先清空详情区 + 列表选中，避免残留旧任务的失败/取消状态。
        // 决策 8-A：切换 Tab 始终回到"无选中 = 空状态提示"，用户主动点击列表项才加载详情。
        Detail.Clear();
        List.ClearSelection();

        List.WorkspaceId = workspaceId ?? string.Empty;
        // SubModeFilter setter 内部已处理"切换时清空选中项 + 重新加载"，
        // 此处再调 LoadAsync 看似冗余，但是 SubModeFilter 引用相同时 setter 会跳过 reload，
        // 由本方法显式 LoadAsync 兜底（与 5B 之前的语义保持一致：tab 切换始终触发列表刷新）。
        List.SubModeFilter = subModes;
        return List.LoadAsync();
    }

    /// <summary>
    /// 启动新任务后立即切换主详情区到该任务，展示运行状态与后续步骤。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="title">任务标题（为空时从引擎加载）。</param>
    /// <param name="initialUserInput">用户原始输入，非空时直接写入 Feed 首条用户消息，无需等待引擎事件。</param>
    public Task ShowTaskAsync(string taskId, string? title = null, string? initialUserInput = null)
    {
        if (string.IsNullOrEmpty(taskId)) return Task.CompletedTask;

        // 新任务直接用 LoadTask（同步初始化，含用户首条消息）
        // 关键顺序：LoadTask 先于 SelectTaskById 执行，确保 _taskId 已设置。
        // SelectTaskById 会触发 OnListPropertyChanged → Detail.LoadAsync(taskId)，
        // 而 LoadAsync 开头有 `if (taskId == _taskId) return;` 守卫，
        // LoadTask 先跑 → _taskId 已等于 taskId → LoadAsync 直接返回，避免竞态覆盖。
        if (!string.IsNullOrEmpty(initialUserInput))
        {
            Detail.LoadTask(taskId, title ?? taskId[..Math.Min(8, taskId.Length)], initialUserInput);
            List.SelectTaskById(taskId);
            return Task.CompletedTask;
        }

        List.SelectTaskById(taskId);
        return Detail.LoadAsync(taskId);
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
