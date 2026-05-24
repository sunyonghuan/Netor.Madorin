using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P2-4：保存常用 Agent 对话框中的单行 ViewModel。
/// P4 过渡：DynamicAgentRecord 已删除，改用简单的构造参数传入。
/// </summary>
public sealed class SaveAgentItemVm : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _newName;
    private string _nameError = string.Empty;

    public SaveAgentItemVm(string name, string responsibility, string instructions, IReadOnlyList<string> requiredTools)
    {
        OriginalName = name ?? string.Empty;
        OriginalResponsibility = responsibility ?? string.Empty;
        OriginalInstructions = instructions ?? string.Empty;
        RequiredTools = requiredTools ?? [];
        ToolsHint = RequiredTools is { Count: > 0 }
            ? $"工具：{string.Join(", ", RequiredTools)}"
            : "工具：（无）";
        _newName = OriginalName;
    }

    public string OriginalName { get; }
    public string OriginalResponsibility { get; }
    public string OriginalInstructions { get; }
    public IReadOnlyList<string> RequiredTools { get; }
    public string ToolsHint { get; }

    /// <summary>用户是否勾选保存这一行。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>用户在 TextBox 输入的目标名称（默认 = OriginalName）。</summary>
    public string NewName
    {
        get => _newName;
        set
        {
            if (SetField(ref _newName, value))
            {
                // 名称改动后清空旧错误，让用户能再次提交
                NameError = string.Empty;
            }
        }
    }

    /// <summary>校验失败时的错误提示，UI 红字显示。空表示无错误。</summary>
    public string NameError
    {
        get => _nameError;
        set
        {
            if (SetField(ref _nameError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_nameError);

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
