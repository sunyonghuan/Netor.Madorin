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
        // Bug 修复（2026-05-17，根因：InitializeComponent 加载 XAML 时 ComboBox.SelectionChanged
        // 立即触发 OnSubModeChanged 默认选中第一项，此时 _vm 还是 null 会抛 NRE 让整个对话框构造失败 →
        // Release/AOT 模式下 Debug.WriteLine 异常被剥离 → 用户看到"无响应"假象）。
        // 修复：必须先初始化 _vm，再 InitializeComponent；OnSubModeChanged 也加 null 防御作为双保险。
        _vm = new NewTaskDialogVm();
        InitializeComponent();
        DataContext = _vm;
    }

    /// <summary>对话框启动前由调用方注入工作区 ID。</summary>
    public string WorkspaceId
    {
        get => _vm.WorkspaceId;
        set => _vm.WorkspaceId = value;
    }

    /// <summary>
    /// 阶段 5B Phase 3：调用方可在显示对话框前预填初始输入文本（用于 Chat→Workflow 桥接）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3。
    /// </summary>
    public string InitialInput
    {
        get => _vm.InitialInput;
        set => _vm.InitialInput = value ?? string.Empty;
    }

    /// <summary>
    /// 阶段 5B Phase 3：调用方可在显示对话框前预填子模式（"Magentic" / "GroupChat" / "ParallelAnalysis"）。
    /// 必须与 UI ComboBox 的 Tag 取值一致；非法值时由 VM 自身忽略。
    /// </summary>
    public string SubMode
    {
        get => _vm.SubMode;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                _vm.SubMode = value;
            }
        }
    }

    /// <summary>提交成功后返回的 taskId（关闭后调用方读取）。</summary>
    public string? CreatedTaskId { get; private set; }

    // ──── 子模式切换 ────

    private void OnSubModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Bug 修复（2026-05-17）双保险：构造期 InitializeComponent 加载 XAML 时 ComboBox 默认选中
        // 第一项会立即触发本事件，此时 _vm 可能还未初始化（取决于 ctor 内字段赋值与 InitializeComponent
        // 的执行顺序）。即使 ctor 已修复，保留此 null 防御让本控件即使被 axaml-only 场景实例化也安全。
        if (_vm is null) return;
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

    /// <summary>
    /// 阶段 6 Phase 3：用户点"知道了"按钮关闭成本警告 Banner。
    /// 决策 6-3-B：30 分钟内不重复显示同一阈值的警告（跨会话持久化到 SystemSettings）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #3。
    /// </summary>
    private void OnDismissCostWarningClick(object? sender, RoutedEventArgs e)
    {
        _vm.DismissCostWarning();
    }
}
