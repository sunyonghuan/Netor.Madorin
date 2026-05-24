using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// 子智能体向用户提问的卡片 ViewModel。
/// 在需求分析/计划制定阶段，子智能体可通过主智能体转发问题给用户。
/// XAML 绑定：WorkflowDetailView.axaml 中 question-card 的 DataContext。
/// </summary>
public sealed class UserQuestionVm : INotifyPropertyChanged
{
    private string _questionText = string.Empty;
    private string _userAnswer = string.Empty;
    private bool _isVisible;
    private bool _isInteractive = true;
    private string _phaseLabel = string.Empty;
    private int _round;

    /// <summary>问题请求唯一 ID（用于匹配回答）。</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>问题文本。</summary>
    public string QuestionText
    {
        get => _questionText;
        set => SetField(ref _questionText, value);
    }

    /// <summary>用户输入的回答（TwoWay 绑定）。</summary>
    public string UserAnswer
    {
        get => _userAnswer;
        set => SetField(ref _userAnswer, value);
    }

    /// <summary>控制卡片显示/隐藏（XAML IsVisible 绑定）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>是否可交互（提交后禁用防重复）。</summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set => SetField(ref _isInteractive, value);
    }

    /// <summary>阶段标签（"需求分析" / "计划制定"）。</summary>
    public string PhaseLabel
    {
        get => _phaseLabel;
        set => SetField(ref _phaseLabel, value);
    }

    /// <summary>对话轮次。</summary>
    public int Round
    {
        get => _round;
        set => SetField(ref _round, value);
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
