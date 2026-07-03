using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作模式孤儿任务恢复提示卡。
/// </summary>
public partial class TaskResumePromptCard : UserControl
{
    private string _taskId = string.Empty;

    public TaskResumePromptCard()
    {
        InitializeComponent();
    }

    public event Action<string>? ResumeRequested;
    public event Action<string>? CancelRequested;

    public string TaskId
    {
        get => _taskId;
        set => _taskId = value ?? string.Empty;
    }

    public string TaskTitle
    {
        get => TitleBlock.Text ?? string.Empty;
        set => TitleBlock.Text = string.IsNullOrWhiteSpace(value)
            ? "上次关闭前还有一个工作任务没有完成。"
            : $"上次关闭前任务“{value}”还没有完成。";
    }

    private void OnResumeClick(object? sender, RoutedEventArgs e)
    {
        ResumeRequested?.Invoke(_taskId);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(_taskId);
    }
}
