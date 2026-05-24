using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// HITL 计划审批卡片 ViewModel。
/// 当 P4 引擎进入"等待用户确认计划"状态时，由 P4TaskDetailVm 构建并赋值给 Approval 属性。
/// XAML 绑定：WorkflowDetailView.axaml 中 approval-card 的 DataContext。
/// </summary>
public sealed class PlanApprovalVm : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _pauseReasonText = string.Empty;
    private string _planText = string.Empty;
    private string? _progressSummary;
    private string _revisionInput = string.Empty;
    private bool _isInteractive = true;
    private bool _isStalled;
    private string? _stallWarning;

    /// <summary>控制卡片显示/隐藏（XAML IsVisible 绑定）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>暂停原因标题（如"等待确认执行计划 (v1, 6步)"）。</summary>
    public string PauseReasonText
    {
        get => _pauseReasonText;
        set => SetField(ref _pauseReasonText, value);
    }

    /// <summary>计划详情文本（步骤列表的格式化摘要）。</summary>
    public string PlanText
    {
        get => _planText;
        set => SetField(ref _planText, value);
    }

    /// <summary>当前进度摘要（修改计划后的新版本显示）。</summary>
    public string? ProgressSummary
    {
        get => _progressSummary;
        set => SetField(ref _progressSummary, value);
    }

    /// <summary>用户输入的修改建议（TwoWay 绑定）。</summary>
    public string RevisionInput
    {
        get => _revisionInput;
        set => SetField(ref _revisionInput, value);
    }

    /// <summary>是否可交互（按钮启用状态）。</summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set => SetField(ref _isInteractive, value);
    }

    /// <summary>是否超时卡住。</summary>
    public bool IsStalled
    {
        get => _isStalled;
        set => SetField(ref _isStalled, value);
    }

    /// <summary>超时警告文本。</summary>
    public string? StallWarning
    {
        get => _stallWarning;
        set => SetField(ref _stallWarning, value);
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
