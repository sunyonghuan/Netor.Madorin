using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// 高风险工具屏蔽项（决策 6-2-A 黑名单模式）。
/// 用户勾选 = 屏蔽该工具；默认不勾选 = 允许。
/// </summary>
public sealed class HighRiskToolItem(string displayName, string toolId, string description) : INotifyPropertyChanged
{
    private bool _isBlocked;

    /// <summary>UI 显示名称。</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>工具标识（格式：pluginId:toolName 或 sys_xxx）。</summary>
    public string ToolId { get; } = toolId;

    /// <summary>工具说明（Tooltip 用）。</summary>
    public string Description { get; } = description;

    /// <summary>是否屏蔽（true = 本次任务不使用该工具）。</summary>
    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (_isBlocked == value) return;
            _isBlocked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
