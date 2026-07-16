using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Media.Imaging;

namespace Netor.Cortana.UI.Controls.Shared.Internal;

/// <summary>
/// 文件树节点类型。
/// </summary>
public enum FileTreeNodeKind
{
    Directory,
    File,
    LoadingPlaceholder
}

/// <summary>
/// 目录节点的懒加载状态。
/// </summary>
public enum FileTreeLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed
}

/// <summary>
/// 文件树节点。
/// </summary>
public sealed class FileTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string _fullPath = string.Empty;
    private string _name = string.Empty;

    public required FileTreeNodeKind Kind { get; init; }

    public required string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public required string FullPath
    {
        get => _fullPath;
        set
        {
            if (_fullPath == value) return;
            _fullPath = value;
            OnPropertyChanged();
        }
    }

    public required Bitmap ClosedIcon { get; set; }
    public Bitmap? ExpandedIcon { get; set; }
    public FileTreeNode? Parent { get; init; }
    public int Depth { get; init; }
    public bool IsLastChild { get; set; }
    public ObservableCollection<FileTreeNode> Children { get; set; } = [];

    /// <summary>
    /// 目录节点的懒加载状态；文件节点固定为 <see cref="FileTreeLoadState.Loaded"/>。
    /// </summary>
    internal FileTreeLoadState LoadState { get; set; } = FileTreeLoadState.NotLoaded;

    /// <summary>
    /// 目录懒加载的取消源，工作区切换或节点被移除时用于取消未完成的装载。
    /// </summary>
    internal System.Threading.CancellationTokenSource? LoadCts { get; set; }

    /// <summary>
    /// 装载时抓取的 workspace 版本号，回 UI 前对比可发现工作区已切换。
    /// </summary>
    internal int WorkspaceGeneration { get; set; }

    public bool IsDirectory => Kind == FileTreeNodeKind.Directory;
    public bool IsPlaceholder => Kind == FileTreeNodeKind.LoadingPlaceholder;

    public Bitmap Icon => IsDirectory && IsExpanded && ExpandedIcon is not null
        ? ExpandedIcon
        : ClosedIcon;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 通知 <see cref="Icon"/> 已根据当前状态发生变化（例如图标缓存更新时使用）。
    /// </summary>
    internal void NotifyIconChanged() => OnPropertyChanged(nameof(Icon));
}
