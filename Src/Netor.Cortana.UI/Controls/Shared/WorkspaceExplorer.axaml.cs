using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

using ConnectorLine = Avalonia.Controls.Shapes.Line;

namespace Netor.Cortana.UI.Controls.Shared;

/// <summary>
/// 工作区文件浏览器控件，负责展示当前工作目录下的文件树，并提供打开、创建、刷新、拖放和右键菜单操作。
/// </summary>
public partial class WorkspaceExplorer : UserControl, INotifyPropertyChanged
{
    /// <summary>
    /// 文件拖放目标的边框颜色。
    /// </summary>
    private static readonly IBrush DragBorderBrush = SolidColorBrush.Parse("#007ACC");

    /// <summary>
    /// 文件拖放目标的背景颜色。
    /// </summary>
    private static readonly IBrush DragBackgroundBrush = SolidColorBrush.Parse("#1a007ACC");

    /// <summary>
    /// 文件树层级辅助线颜色。
    /// </summary>
    private static readonly IBrush TreeConnectorBrush = SolidColorBrush.Parse("#66777777");

    /// <summary>
    /// 文件夹节点默认图标。
    /// </summary>
    private static readonly Bitmap FolderIcon = LoadAvaloniaBitmap(AppBranding.AssetUri("folder.png"));

    /// <summary>
    /// 文件节点默认图标。
    /// </summary>
    private static readonly Bitmap FileIcon = LoadAvaloniaBitmap(AppBranding.AssetUri("file.png"));

    /// <summary>
    /// 本地文件图标主题解析器。
    /// </summary>
    private static readonly LocalFileIconTheme FileIconTheme = LocalFileIconTheme.Load(FolderIcon, FileIcon);

    /// <summary>
    /// 监听当前工作目录下文件和目录变化的文件系统监视器。
    /// </summary>
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// 当前工作目录的完整路径。
    /// </summary>
    private string _workspaceDirectory = string.Empty;

    /// <summary>
    /// 顶部工具栏展示的工作区标题。
    /// </summary>
    private string _workspaceTitle = "工作台";

    /// <summary>
    /// 全局事件订阅器，用于监听工作目录变更事件。
    /// </summary>
    private readonly ISubscriber _subscriber;

    /// <summary>
    /// 右键复制操作暂存的文件或目录路径。
    /// </summary>
    private readonly List<string> _clipboardPaths = [];

    /// <summary>
    /// 当前拖放悬停的树节点控件。
    /// </summary>
    private TreeViewItem? _activeDropTargetItem;

    /// <summary>
    /// 标识当前拖放目标是否为文件树根区域。
    /// </summary>
    private bool _isRootDropTarget;

    /// <summary>
    /// 文件树层级辅助线是否处于可见状态。
    /// </summary>
    private bool _areTreeConnectorsVisible;

    /// <summary>
    /// 防止布局频繁变化时重复排队刷新辅助线。
    /// </summary>
    private bool _treeConnectorUpdateQueued;

    /// <summary>
    /// 上一次辅助线布局签名，用于避免覆盖层自身触发布局后重复重画。
    /// </summary>
    private int _treeConnectorLayoutHash;

    // WorkspaceChanged 事件已迁移到 EventHub（Events.OnWorkspaceChanged）

    /// <summary>
    /// 请求将文件路径列表添加为聊天附件，由 MainWindow 订阅。
    /// </summary>
    public event Action<IReadOnlyList<string>>? AttachmentRequested;

    /// <summary>
    /// P3-2：请求将文件路径列表添加为工作流附件，由 MainWindow 订阅。
    /// </summary>
    public event Action<IReadOnlyList<string>>? WorkflowAttachmentRequested;

    /// <summary>
    /// P3-2：请求将文件路径列表添加为群聊附件，由 MainWindow 订阅。
    /// </summary>
    public event Action<IReadOnlyList<string>>? GroupChatAttachmentRequested;

    /// <summary>
    /// 请求折叠左侧工作台面板，由 MainWindow 处理实际布局切换。
    /// </summary>
    public event Action? WorkspacePanelCollapseRequested;

    /// <summary>
    /// 初始化工作区文件浏览器控件，并注册拖放处理和工作目录变更事件订阅。
    /// </summary>
    public WorkspaceExplorer()
    {
        InitializeComponent();
        DataContext = this;
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => WorkspaceDirectory = args.Path);
            return Task.FromResult(false);
        });
        FileTree.AddHandler(DragDrop.DragEnterEvent, OnTreeDragEnter);
        FileTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        FileTree.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
        FileTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        FileTree.PointerEntered += OnFileTreePointerEntered;
        FileTree.PointerExited += OnFileTreePointerExited;
        FileTree.LayoutUpdated += OnFileTreeLayoutUpdated;
    }

    /// <summary>
    /// 文件树根节点集合。
    /// </summary>
    public ObservableCollection<FileTreeNode> TreeNodes { get; } = [];

    /// <summary>
    /// 顶部工具栏显示的当前工作目录名称。
    /// </summary>
    public string WorkspaceTitle
    {
        get => _workspaceTitle;
        private set => SetField(ref _workspaceTitle, value);
    }

    /// <summary>
    /// 当前工作目录。设置后会刷新文件树，并同步更新顶部标题。
    /// </summary>
    public string WorkspaceDirectory
    {
        get => _workspaceDirectory;
        set
        {
            // 统一去掉尾部分隔符，避免 Uri.LocalPath 产生的尾 \ 导致路径比较失败
            var normalized = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(_workspaceDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            _workspaceDirectory = normalized;
            UpdateWorkspaceTitle();
            LoadTree();
        }
    }

    /// <summary>
    /// 根据当前工作目录路径更新工具栏标题。
    /// </summary>
    private void UpdateWorkspaceTitle()
    {
        WorkspaceTitle = string.IsNullOrWhiteSpace(_workspaceDirectory)
            ? "工作台"
            : Path.GetFileName(_workspaceDirectory) switch
            {
                { Length: > 0 } name => name,
                _ => _workspaceDirectory
            };
    }

            /// <summary>
            /// 属性变更通知事件的内部存储，避免与 AvaloniaObject.PropertyChanged 冲突。
            /// </summary>
            private event PropertyChangedEventHandler? NotifyPropertyChanged;

            /// <summary>
            /// 属性变更通知事件，用于刷新 XAML 绑定。
            /// </summary>
            event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
            {
                add => NotifyPropertyChanged += value;
                remove => NotifyPropertyChanged -= value;
            }

            /// <summary>
            /// 设置字段值并触发属性变更通知。
            /// </summary>
            /// <typeparam name="T">字段类型。</typeparam>
            /// <param name="field">需要更新的字段引用。</param>
            /// <param name="value">新的字段值。</param>
            /// <param name="propertyName">发生变化的属性名称。</param>
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        NotifyPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ──────── 文件树构建 ────────

    private void LoadTree()
    {
        TreeNodes.Clear();
        DisposeWatcher();

        if (string.IsNullOrEmpty(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        foreach (var node in ScanDirectory(_workspaceDirectory, _workspaceDirectory, parent: null, depth: 0))
            TreeNodes.Add(node);

        FileTree.ItemsSource = TreeNodes;
        ScheduleTreeConnectorUpdate();
        StartWatcher();
    }

    private static List<FileTreeNode> ScanDirectory(string path, string workspaceRoot, FileTreeNode? parent, int depth)
    {
        var result = new List<FileTreeNode>();

        try
        {
            var dirInfo = new DirectoryInfo(path);

            // 文件夹在前，按名称排序；根层点目录多为平台/工作区设置目录，不在文件树中展示。
            var dirs = dirInfo.GetDirectories()
                .Where(d => ShouldShowDirectory(d.Name, depth == 0))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                var relativePath = GetRelativeThemePath(workspaceRoot, dir.FullName);
                var node = new FileTreeNode
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    ClosedIcon = FileIconTheme.ResolveFolderIcon(dir.Name, relativePath, expanded: false, isRootLevel: depth == 0),
                    ExpandedIcon = FileIconTheme.ResolveFolderIcon(dir.Name, relativePath, expanded: true, isRootLevel: depth == 0),
                    Parent = parent,
                    Depth = depth
                };
                node.Children = new ObservableCollection<FileTreeNode>(ScanDirectory(dir.FullName, workspaceRoot, node, depth + 1));
                result.Add(node);
            }

            var files = dirInfo.GetFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var relativePath = GetRelativeThemePath(workspaceRoot, file.FullName);
                result.Add(new FileTreeNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    ClosedIcon = FileIconTheme.ResolveFileIcon(file.Name, relativePath),
                    Parent = parent,
                    Depth = depth
                });
            }

            MarkLastChild(result);
        }
        catch
        {
            // 权限不足等情况静默忽略
        }

        return result;
    }

    private static bool ShouldShowDirectory(string directoryName, bool isRootLevel)
    {
        if (isRootLevel && directoryName.StartsWith(".", StringComparison.Ordinal))
            return false;

        return !IsHiddenRepositoryDirectory(directoryName);
    }

    private static bool IsHiddenRepositoryDirectory(string directoryName)
    {
        return directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               directoryName.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
               directoryName.Equals(".hg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHiddenDirectory(string relativePath)
    {
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (!ShouldShowDirectory(segments[i], i == 0))
                return true;
        }

        return false;
    }

    private static string GetRelativeThemePath(string workspaceRoot, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(workspaceRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }

    private static void MarkLastChild(List<FileTreeNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
            nodes[i].IsLastChild = i == nodes.Count - 1;
    }

    private static Bitmap LoadAvaloniaBitmap(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    private void OnFileTreePointerEntered(object? sender, PointerEventArgs e)
    {
        _areTreeConnectorsVisible = true;
        TreeConnectorLayer.Opacity = 1;
        ScheduleTreeConnectorUpdate();
    }

    private void OnFileTreePointerExited(object? sender, PointerEventArgs e)
    {
        _areTreeConnectorsVisible = false;
        TreeConnectorLayer.Opacity = 0;
        _treeConnectorLayoutHash = 0;
        TreeConnectorLayer.Children.Clear();
    }

    private void OnFileTreeLayoutUpdated(object? sender, EventArgs e)
    {
        if (_areTreeConnectorsVisible)
            ScheduleTreeConnectorUpdate();
    }

    private void ScheduleTreeConnectorUpdate()
    {
        if (!_areTreeConnectorsVisible || _treeConnectorUpdateQueued)
            return;

        _treeConnectorUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _treeConnectorUpdateQueued = false;
            if (_areTreeConnectorsVisible)
                UpdateTreeConnectors();
        }, DispatcherPriority.Background);
    }

    private void UpdateTreeConnectors()
    {
        if (!AreClose(TreeConnectorLayer.Width, FileTree.Bounds.Width))
            TreeConnectorLayer.Width = FileTree.Bounds.Width;
        if (!AreClose(TreeConnectorLayer.Height, FileTree.Bounds.Height))
            TreeConnectorLayer.Height = FileTree.Bounds.Height;

        var items = FileTree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .Select(CreateConnectorTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .OrderBy(target => target.Top)
            .ToList();

        if (items.Count == 0)
        {
            _treeConnectorLayoutHash = 0;
            TreeConnectorLayer.Children.Clear();
            return;
        }

        var metrics = EstimateConnectorMetrics(items);
        var layoutHash = CreateConnectorLayoutHash(items, metrics);
        if (layoutHash == _treeConnectorLayoutHash && TreeConnectorLayer.Children.Count > 0)
            return;

        _treeConnectorLayoutHash = layoutHash;
        TreeConnectorLayer.Children.Clear();
        foreach (var target in items)
            DrawConnector(target, metrics);
    }

    private static int CreateConnectorLayoutHash(
        IReadOnlyList<TreeConnectorTarget> targets,
        TreeConnectorMetrics metrics)
    {
        var hash = new HashCode();
        hash.Add((int)Math.Round(metrics.RootX));
        hash.Add((int)Math.Round(metrics.Indent));
        foreach (var target in targets)
        {
            hash.Add(target.Node.FullPath, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)Math.Round(target.Top));
            hash.Add((int)Math.Round(target.Bottom));
            hash.Add((int)Math.Round(target.IconLeft));
            hash.Add(target.Node.IsLastChild);
        }

        return hash.ToHashCode();
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.5;
    }

    private TreeConnectorTarget? CreateConnectorTarget(TreeViewItem item)
    {
        if (item.DataContext is not FileTreeNode node)
            return null;

        if (!IsNodeVisibleInExpandedTree(node) || !item.IsVisible || item.Bounds.Height < 1)
            return null;

        var itemPoint = item.TranslatePoint(new Avalonia.Point(0, 0), TreeConnectorLayer);
        if (itemPoint is null)
            return null;

        var icon = item.GetVisualDescendants().OfType<Image>().FirstOrDefault();
        var iconPoint = icon?.TranslatePoint(new Avalonia.Point(0, 0), TreeConnectorLayer);
        if (iconPoint is null)
            return null;

        var top = itemPoint.Value.Y;
        var bottom = top + item.Bounds.Height;
        var middle = top + item.Bounds.Height / 2;
        var iconLeft = iconPoint.Value.X;

        if (bottom < 0 || top > FileTree.Bounds.Height || iconLeft < 0)
            return null;

        return new TreeConnectorTarget(node, top, bottom, middle, iconLeft);
    }

    private static bool IsNodeVisibleInExpandedTree(FileTreeNode node)
    {
        var ancestor = node.Parent;
        while (ancestor is not null)
        {
            if (!ancestor.IsExpanded)
                return false;

            ancestor = ancestor.Parent;
        }

        return true;
    }

    private static TreeConnectorMetrics EstimateConnectorMetrics(IReadOnlyList<TreeConnectorTarget> targets)
    {
        var rootIconLeft = targets
            .Where(target => target.Node.Depth == 0)
            .Select(target => target.IconLeft)
            .DefaultIfEmpty(28)
            .Min();

        var firstChildIconLeft = targets
            .Where(target => target.Node.Depth == 1)
            .Select(target => target.IconLeft)
            .DefaultIfEmpty(rootIconLeft + 20)
            .Min();

        var rootX = Math.Max(4, firstChildIconLeft - 20);

        var indent = targets
            .Where(target => target.Node.Parent is not null)
            .Select(target =>
            {
                var parent = targets.FirstOrDefault(candidate => ReferenceEquals(candidate.Node, target.Node.Parent));
                return parent is null ? 0 : target.IconLeft - parent.IconLeft;
            })
            .Where(value => value > 12)
            .DefaultIfEmpty(22)
            .Average();

        return new TreeConnectorMetrics(rootX, Math.Clamp(indent, 18, 28));
    }

    private void DrawConnector(TreeConnectorTarget target, TreeConnectorMetrics metrics)
    {
        var branchX = GetConnectorX(target.Node.Depth, metrics);
        DrawVerticalLine(branchX, target.Top, target.Node.IsLastChild ? target.Middle : target.Bottom);

        if (target.Node.Depth > 0)
            DrawHorizontalLine(branchX, Math.Max(branchX, target.IconLeft - 4), target.Middle);

        var ancestor = target.Node.Parent;
        while (ancestor is not null)
        {
            if (!ancestor.IsLastChild)
                DrawVerticalLine(GetConnectorX(ancestor.Depth, metrics), target.Top, target.Bottom);

            ancestor = ancestor.Parent;
        }
    }

    private static double GetConnectorX(int depth, TreeConnectorMetrics metrics)
    {
        return metrics.RootX + Math.Max(0, depth - 1) * metrics.Indent;
    }

    private void DrawVerticalLine(double x, double y1, double y2)
    {
        if (y2 <= y1)
            return;

        TreeConnectorLayer.Children.Add(CreateConnectorLine(x, y1, x, y2));
    }

    private void DrawHorizontalLine(double x1, double x2, double y)
    {
        if (x2 <= x1)
            return;

        TreeConnectorLayer.Children.Add(CreateConnectorLine(x1, y, x2, y));
    }

    private static ConnectorLine CreateConnectorLine(double x1, double y1, double x2, double y2)
    {
        return new ConnectorLine
        {
            StartPoint = new Avalonia.Point(x1, y1),
            EndPoint = new Avalonia.Point(x2, y2),
            Stroke = TreeConnectorBrush,
            StrokeThickness = 1,
            StrokeDashArray = [2, 3],
            IsHitTestVisible = false
        };
    }

    // ──────── FileSystemWatcher ────────

    private void StartWatcher()
    {
        if (string.IsNullOrEmpty(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        _watcher = new FileSystemWatcher(_workspaceDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // 忽略不展示的目录，避免根层点目录变动时把它们刷新回文件树。
        var relative = Path.GetRelativePath(_workspaceDirectory, e.FullPath);
        if (ContainsHiddenDirectory(relative))
            return;

        Dispatcher.UIThread.Post(() => RefreshTreeByChange(e));
    }

    private void RefreshTreeByChange(FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        // 文件/目录新增或删除时，刷新其父目录；重命名时，刷新旧父目录和新父目录。
        if (e is RenamedEventArgs renamed)
        {
            RefreshDirectory(Path.GetDirectoryName(renamed.OldFullPath));
            RefreshDirectory(Path.GetDirectoryName(renamed.FullPath));
            return;
        }

        RefreshDirectory(Path.GetDirectoryName(e.FullPath));
    }

    private void RefreshDirectory(string? targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        // 记住当前选中节点
        var selectedPath = (FileTree.SelectedItem as FileTreeNode)?.FullPath;

        // 统一去掉尾部分隔符再比较
        var normalizedTarget = targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedTarget, _workspaceDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var rootExpandedState = TreeNodes
                .Where(t => t.IsDirectory)
                .ToDictionary(t => t.FullPath, t => t.IsExpanded, StringComparer.OrdinalIgnoreCase);

            TreeNodes.Clear();
            foreach (var node in ScanDirectory(_workspaceDirectory, _workspaceDirectory, parent: null, depth: 0))
            {
                if (node.IsDirectory && rootExpandedState.TryGetValue(node.FullPath, out var expanded))
                    node.IsExpanded = expanded;

                TreeNodes.Add(node);
            }

            RestoreSelection(selectedPath);
            return;
        }

        var dirNode = FindNodeByPath(TreeNodes, normalizedTarget);
        if (dirNode is null || !dirNode.IsDirectory)
            return;

        var childExpandedState = dirNode.Children
            .Where(t => t.IsDirectory)
            .ToDictionary(t => t.FullPath, t => t.IsExpanded, StringComparer.OrdinalIgnoreCase);

        var children = ScanDirectory(normalizedTarget, _workspaceDirectory, dirNode, dirNode.Depth + 1);

        dirNode.Children.Clear();
        foreach (var child in children)
        {
            if (child.IsDirectory && childExpandedState.TryGetValue(child.FullPath, out var expanded))
                child.IsExpanded = expanded;

            dirNode.Children.Add(child);
        }

        RestoreSelection(selectedPath);
    }

    private void RestoreSelection(string? selectedPath)
    {
        if (selectedPath is null) return;
        var target = FindNodeByPath(TreeNodes, selectedPath);
        if (target is not null)
            FileTree.SelectedItem = target;
    }

    private static FileTreeNode? FindNodeByPath(IEnumerable<FileTreeNode> nodes, string fullPath)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return node;

            if (!node.IsDirectory || node.Children.Count == 0)
                continue;

            var found = FindNodeByPath(node.Children, fullPath);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void DisposeWatcher()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnFileSystemChanged;
        _watcher.Deleted -= OnFileSystemChanged;
        _watcher.Renamed -= OnFileSystemChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    // ──────── 工具栏事件 ────────

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false
        });

        if (result.Count == 0) return;

        var newPath = result[0].Path.LocalPath;

        // 通过 EventHub 广播，所有订阅者自动同步
        var publisher = App.Services.GetRequiredService<IPublisher>();
        publisher.Publish(Events.OnWorkspaceChanged, new WorkspaceChangedArgs(newPath));
    }

    private async void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "新建文件",
            SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(_workspaceDirectory),
            SuggestedFileName = "untitled.txt"
        });

        if (result is null) return;

        var filePath = result.Path.LocalPath;
        if (!File.Exists(filePath))
            await File.WriteAllTextAsync(filePath, string.Empty);
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        LoadTree();
    }

    private void OnCollapseAllClick(object? sender, RoutedEventArgs e)
    {
        CollapseAll(TreeNodes);
    }

    private void OnWorkspacePanelCollapseClick(object? sender, RoutedEventArgs e)
    {
        WorkspacePanelCollapseRequested?.Invoke();
    }

    private static void CollapseAll(IEnumerable<FileTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = false;
            if (node.Children.Count > 0)
                CollapseAll(node.Children);
        }
    }

    // ──────── 文件双击打开 ────────

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode node || node.IsDirectory)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });
        }
        catch
        {
            // 无关联程序等情况静默忽略
        }
    }

    // ──────── 右键菜单 ────────

    /// <summary>
    /// 获取当前选中的所有节点（多选支持）。
    /// </summary>
    private List<FileTreeNode> GetSelectedNodes()
    {
        var nodes = new List<FileTreeNode>();
        if (FileTree.SelectedItems is null) return nodes;

        foreach (var item in FileTree.SelectedItems)
        {
            if (item is FileTreeNode node)
                nodes.Add(node);
        }
        return nodes;
    }

    /// <summary>
    /// 右键菜单打开前，根据选中节点类型控制菜单项可见性。
    /// </summary>
    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var nodes = GetSelectedNodes();
        if (nodes.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var isSingle = nodes.Count == 1;
        var singleNode = isSingle ? nodes[0] : null;
        var hasFile = nodes.Any(n => !n.IsDirectory);
        var hasDir = nodes.Any(n => n.IsDirectory);
        var allFiles = nodes.All(n => !n.IsDirectory);

        // 打开：仅单选文件
        MenuOpen.IsVisible = isSingle && !singleNode!.IsDirectory;
        // 在资源管理器中显示：任意选择
        MenuOpenInExplorer.IsVisible = true;
        // 在终端中打开：仅单选文件夹
        MenuOpenInTerminal.IsVisible = isSingle && singleNode!.IsDirectory;
        // 发送到聊天/工作流/群聊附件：P3-2 支持文件和文件夹
        MenuSendToChat.IsVisible = true;
        MenuSendToWorkflow.IsVisible = true;
        MenuSendToGroupChat.IsVisible = true;
        // 新建文件/文件夹：仅单选文件夹
        MenuNewFile.IsVisible = isSingle && singleNode!.IsDirectory;
        MenuNewFolder.IsVisible = isSingle && singleNode!.IsDirectory;
        // 复制文件：任意选择
        MenuCopy.IsVisible = true;
        // 粘贴：仅单选文件夹 + 剪贴板有内容
        MenuPaste.IsVisible = isSingle && singleNode!.IsDirectory && _clipboardPaths.Count > 0;
        // 重命名：仅单选
        MenuRename.IsVisible = isSingle;
        // 删除：任意选择
        MenuDelete.IsVisible = true;
        // 复制路径：任意选择
        MenuCopyPath.IsVisible = true;
        MenuCopyRelPath.IsVisible = true;
    }

    private void OnMenuOpenClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode node || node.IsDirectory) return;
        try { Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true }); } catch { }
    }

    private void OnMenuOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        foreach (var node in GetSelectedNodes())
        {
            try
            {
                if (node.IsDirectory)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.FullPath}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OnMenuOpenInTerminalClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode { IsDirectory: true } node) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoExit -Command \"Set-Location '{node.FullPath}'\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnMenuSendToChatClick(object? sender, RoutedEventArgs e)
    {
        // P3-2：支持文件和文件夹
        var paths = GetSelectedNodes().Select(n => n.FullPath).ToList();
        if (paths.Count > 0)
            AttachmentRequested?.Invoke(paths);
    }

    private void OnMenuSendToWorkflowClick(object? sender, RoutedEventArgs e)
    {
        var paths = GetSelectedNodes().Select(n => n.FullPath).ToList();
        if (paths.Count > 0)
            WorkflowAttachmentRequested?.Invoke(paths);
    }

    private void OnMenuSendToGroupChatClick(object? sender, RoutedEventArgs e)
    {
        var paths = GetSelectedNodes().Select(n => n.FullPath).ToList();
        if (paths.Count > 0)
            GroupChatAttachmentRequested?.Invoke(paths);
    }

    private async void OnMenuNewFileClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode { IsDirectory: true } dirNode) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "新建文件",
            SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(dirNode.FullPath),
            SuggestedFileName = "untitled.txt"
        });

        if (result is null) return;
        var filePath = result.Path.LocalPath;
        if (!File.Exists(filePath))
            await File.WriteAllTextAsync(filePath, string.Empty);
    }

    private void OnMenuNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode { IsDirectory: true } dirNode) return;

        var baseName = "新建文件夹";
        var targetPath = Path.Combine(dirNode.FullPath, baseName);
        var counter = 1;
        while (Directory.Exists(targetPath))
        {
            targetPath = Path.Combine(dirNode.FullPath, $"{baseName} ({counter++})");
        }

        try
        {
            Directory.CreateDirectory(targetPath);
        }
        catch { }
    }

    private void OnMenuCopyClick(object? sender, RoutedEventArgs e)
    {
        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(GetSelectedNodes().Select(n => n.FullPath));
    }

    private void OnMenuPasteClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode { IsDirectory: true } dirNode) return;
        if (_clipboardPaths.Count == 0) return;

        CopyPathsToDirectory(_clipboardPaths, dirNode.FullPath);
    }

    private void CopyPathsToDirectory(IEnumerable<string> sourcePaths, string targetDirectory)
    {
        foreach (var sourcePath in sourcePaths)
        {
            try
            {
                var name = Path.GetFileName(sourcePath);
                var destPath = Path.Combine(targetDirectory, name);

                // 目标已存在时自动加序号
                if (File.Exists(destPath) || Directory.Exists(destPath))
                {
                    var nameOnly = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    var counter = 1;
                    do
                    {
                        destPath = Path.Combine(targetDirectory, $"{nameOnly} - 副本{(counter > 1 ? $" ({counter})" : "")}{ext}");
                        counter++;
                    } while (File.Exists(destPath) || Directory.Exists(destPath));
                }

                if (File.Exists(sourcePath))
                    File.Copy(sourcePath, destPath);
                else if (Directory.Exists(sourcePath))
                    CopyDirectoryRecursive(sourcePath, destPath);
            }
            catch { }
        }
    }

    private void OnTreeDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDropVisual(e);
        e.Handled = true;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropVisual();
            e.Handled = true;
            return;
        }

        e.DragEffects = TryResolveDropDirectory(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        UpdateDropVisual(e);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object? sender, RoutedEventArgs e)
    {
        ClearDropVisual();
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (!TryResolveDropDirectory(e, out var targetDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            var items = e.DataTransfer.TryGetFiles();
            if (items is null)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            var sourcePaths = items
                .Select(i => i.Path?.LocalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sourcePaths.Count == 0)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            await Task.Run(() => CopyPathsToDirectory(sourcePaths, targetDirectory));
            e.DragEffects = DragDropEffects.Copy;
        }
        finally
        {
            ClearDropVisual();
            e.Handled = true;
        }
    }

    private bool TryResolveDropDirectory(DragEventArgs e, out string targetDirectory)
    {
        targetDirectory = string.Empty;

        if (!e.DataTransfer.Contains(DataFormat.File))
            return false;

        var targetNode = ResolveTargetNodeFromDragEvent(e);
        if (targetNode is null)
        {
            if (string.IsNullOrWhiteSpace(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
                return false;

            targetDirectory = _workspaceDirectory;
            return true;
        }

        if (targetNode.IsDirectory)
        {
            targetDirectory = targetNode.FullPath;
            return Directory.Exists(targetDirectory);
        }

        var parentDirectory = Path.GetDirectoryName(targetNode.FullPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            return false;

        targetDirectory = parentDirectory;
        return true;
    }

    private FileTreeNode? ResolveTargetNodeFromDragEvent(DragEventArgs e)
    {
        var sourceVisual = e.Source as Visual;
        if (sourceVisual is null)
            return null;

        var item = sourceVisual.FindAncestorOfType<TreeViewItem>();
        return item?.DataContext as FileTreeNode;
    }

    private void UpdateDropVisual(DragEventArgs e)
    {
        var sourceVisual = e.Source as Visual;
        var currentItem = sourceVisual?.FindAncestorOfType<TreeViewItem>();

        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            ClearDropVisual();
            return;
        }

        if (currentItem is not null && currentItem.DataContext is FileTreeNode)
        {
            if (!ReferenceEquals(_activeDropTargetItem, currentItem))
            {
                _activeDropTargetItem?.Classes.Remove("drop-target");
                _activeDropTargetItem = currentItem;
                _activeDropTargetItem.Classes.Add("drop-target");
            }

            if (_isRootDropTarget)
            {
                _isRootDropTarget = false;
                FileTree.BorderBrush = Brushes.Transparent;
                FileTree.Background = Brushes.Transparent;
            }

            return;
        }

        _activeDropTargetItem?.Classes.Remove("drop-target");
        _activeDropTargetItem = null;

        if (!_isRootDropTarget)
        {
            _isRootDropTarget = true;
            FileTree.BorderBrush = DragBorderBrush;
            FileTree.Background = DragBackgroundBrush;
        }
    }

    private void ClearDropVisual()
    {
        _activeDropTargetItem?.Classes.Remove("drop-target");
        _activeDropTargetItem = null;

        if (_isRootDropTarget)
        {
            _isRootDropTarget = false;
            FileTree.BorderBrush = Brushes.Transparent;
            FileTree.Background = Brushes.Transparent;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private async void OnMenuRenameClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileTreeNode node) return;

        var selectionEnd = node.IsDirectory
            ? node.Name.Length
            : Path.GetFileNameWithoutExtension(node.Name).Length;
        var newName = (await UiPromptService.ShowInputAsync(
            this,
            "重命名",
            node.Name,
            selectionStart: 0,
            selectionEnd: selectionEnd))?.Trim();

        if (string.IsNullOrEmpty(newName) || newName == node.Name) return;

        try
        {
            var parentDir = Path.GetDirectoryName(node.FullPath)!;
            var newPath = Path.Combine(parentDir, newName);

            if (node.IsDirectory)
                Directory.Move(node.FullPath, newPath);
            else
                File.Move(node.FullPath, newPath);
        }
        catch { }
    }

    private async void OnMenuDeleteClick(object? sender, RoutedEventArgs e)
    {
        var nodes = GetSelectedNodes();
        if (nodes.Count == 0) return;

        var message = nodes.Count == 1
            ? $"确定要删除 \"{nodes[0].Name}\" 吗？"
            : $"确定要删除选中的 {nodes.Count} 个项目吗？";

        if (nodes.Any(n => n.IsDirectory))
            message += "\n\n文件夹将被递归删除，此操作不可撤销。";

        var confirmed = await UiPromptService.ShowConfirmAsync(
            this,
            "确认删除",
            message,
            confirmText: "删除",
            isDanger: true);

        if (!confirmed) return;

        foreach (var node in nodes)
        {
            try
            {
                if (node.IsDirectory)
                    Directory.Delete(node.FullPath, recursive: true);
                else
                    File.Delete(node.FullPath);
            }
            catch { }
        }
    }

    private async void OnMenuCopyPathClick(object? sender, RoutedEventArgs e)
    {
        var paths = string.Join(Environment.NewLine, GetSelectedNodes().Select(n => n.FullPath));
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
            await topLevel.Clipboard.SetTextAsync(paths);
    }

    private async void OnMenuCopyRelPathClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_workspaceDirectory)) return;
        var paths = string.Join(Environment.NewLine,
            GetSelectedNodes().Select(n => Path.GetRelativePath(_workspaceDirectory, n.FullPath)));
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is not null)
            await topLevel.Clipboard.SetTextAsync(paths);
    }

    // ──────── 清理 ────────

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        ClearDropVisual();
        DisposeWatcher();
        base.OnUnloaded(e);
    }
}

/// <summary>
/// 文件树节点。
/// </summary>
public sealed class FileTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required Bitmap ClosedIcon { get; init; }
    public Bitmap? ExpandedIcon { get; init; }
    public FileTreeNode? Parent { get; init; }
    public int Depth { get; init; }
    public bool IsLastChild { get; set; }
    public ObservableCollection<FileTreeNode> Children { get; set; } = [];

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
}

internal sealed record TreeConnectorTarget(
    FileTreeNode Node,
    double Top,
    double Bottom,
    double Middle,
    double IconLeft);

internal sealed record TreeConnectorMetrics(
    double RootX,
    double Indent);

internal sealed class LocalFileIconTheme
{
    private const string ThemeRelativePath = "Resources\\FileIcons\\icon-theme.json";
    private const string OverridesFileName = "local-overrides.json";

    private readonly Dictionary<string, string> _iconPaths;
    private readonly Dictionary<string, string> _fileExtensions;
    private readonly Dictionary<string, string> _fileNames;
    private readonly Dictionary<string, string> _folderNames;
    private readonly Dictionary<string, string> _folderNamesExpanded;
    private readonly Dictionary<string, string> _rootFolderNames;
    private readonly Dictionary<string, string> _rootFolderNamesExpanded;
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Bitmap _fallbackFolderIcon;
    private readonly Bitmap _fallbackFileIcon;
    private readonly string _themeRoot;
    private readonly string _defaultFolderIconId;
    private readonly string _defaultFolderExpandedIconId;
    private readonly string _defaultFileIconId;

    private LocalFileIconTheme(
        string themeRoot,
        Dictionary<string, string> iconPaths,
        Dictionary<string, string> fileExtensions,
        Dictionary<string, string> fileNames,
        Dictionary<string, string> folderNames,
        Dictionary<string, string> folderNamesExpanded,
        Dictionary<string, string> rootFolderNames,
        Dictionary<string, string> rootFolderNamesExpanded,
        string defaultFolderIconId,
        string defaultFolderExpandedIconId,
        string defaultFileIconId,
        Bitmap fallbackFolderIcon,
        Bitmap fallbackFileIcon)
    {
        _themeRoot = themeRoot;
        _iconPaths = iconPaths;
        _fileExtensions = fileExtensions;
        _fileNames = fileNames;
        _folderNames = folderNames;
        _folderNamesExpanded = folderNamesExpanded;
        _rootFolderNames = rootFolderNames;
        _rootFolderNamesExpanded = rootFolderNamesExpanded;
        _defaultFolderIconId = defaultFolderIconId;
        _defaultFolderExpandedIconId = defaultFolderExpandedIconId;
        _defaultFileIconId = defaultFileIconId;
        _fallbackFolderIcon = fallbackFolderIcon;
        _fallbackFileIcon = fallbackFileIcon;
    }

    public static LocalFileIconTheme Load(Bitmap fallbackFolderIcon, Bitmap fallbackFileIcon)
    {
        var themePath = Path.Combine(AppContext.BaseDirectory, ThemeRelativePath);
        if (!File.Exists(themePath))
            return Empty(fallbackFolderIcon, fallbackFileIcon);

        try
        {
            using var stream = File.OpenRead(themePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var themeRoot = Path.GetDirectoryName(themePath) ?? AppContext.BaseDirectory;
            var iconPaths = ReadIconDefinitions(root);
            var fileExtensions = ReadStringMap(root, "fileExtensions");
            var fileNames = ReadStringMap(root, "fileNames");
            var folderNames = ReadStringMap(root, "folderNames");
            var folderNamesExpanded = ReadStringMap(root, "folderNamesExpanded");
            var rootFolderNames = ReadStringMap(root, "rootFolderNames");
            var rootFolderNamesExpanded = ReadStringMap(root, "rootFolderNamesExpanded");

            ApplyLocalOverrides(
                themeRoot,
                fileExtensions,
                fileNames,
                folderNames,
                folderNamesExpanded,
                rootFolderNames,
                rootFolderNamesExpanded);

            return new LocalFileIconTheme(
                themeRoot,
                iconPaths,
                fileExtensions,
                fileNames,
                folderNames,
                folderNamesExpanded,
                rootFolderNames,
                rootFolderNamesExpanded,
                ReadString(root, "folder", "folder"),
                ReadString(root, "folderExpanded", "folder-open"),
                ReadString(root, "file", "file"),
                fallbackFolderIcon,
                fallbackFileIcon);
        }
        catch
        {
            return Empty(fallbackFolderIcon, fallbackFileIcon);
        }
    }

    public Bitmap ResolveFolderIcon(string folderName, string relativePath, bool expanded, bool isRootLevel)
    {
        var normalized = NormalizeKey(folderName);
        var normalizedPath = NormalizeKey(relativePath);
        var rootMap = expanded ? _rootFolderNamesExpanded : _rootFolderNames;
        if (isRootLevel && TryResolveFolderMap(rootMap, normalized, normalizedPath, out var iconId))
            return ResolveIcon(iconId, _fallbackFolderIcon);

        var map = expanded ? _folderNamesExpanded : _folderNames;
        if (TryResolveFolderMap(map, normalized, normalizedPath, out iconId))
            return ResolveIcon(iconId, _fallbackFolderIcon);

        if (expanded &&
            ((isRootLevel && TryResolveFolderMap(_rootFolderNames, normalized, normalizedPath, out var closedIconId)) ||
             TryResolveFolderMap(_folderNames, normalized, normalizedPath, out closedIconId)))
        {
            var openIconId = closedIconId.EndsWith("-open", StringComparison.OrdinalIgnoreCase)
                ? closedIconId
                : $"{closedIconId}-open";
            if (_iconPaths.ContainsKey(openIconId))
                return ResolveIcon(openIconId, _fallbackFolderIcon);
        }

        return ResolveIcon(expanded ? _defaultFolderExpandedIconId : _defaultFolderIconId, _fallbackFolderIcon);
    }

    public Bitmap ResolveFileIcon(string fileName, string relativePath)
    {
        var normalized = NormalizeKey(fileName);
        var normalizedPath = NormalizeKey(relativePath);
        if (_fileNames.TryGetValue(normalizedPath, out var iconId) ||
            _fileNames.TryGetValue(normalized, out iconId))
            return ResolveIcon(iconId, _fallbackFileIcon);

        foreach (var extension in EnumerateExtensionCandidates(normalized))
        {
            if (_fileExtensions.TryGetValue(extension, out iconId))
                return ResolveIcon(iconId, _fallbackFileIcon);
        }

        return ResolveIcon(_defaultFileIconId, _fallbackFileIcon);
    }

    private Bitmap ResolveIcon(string iconId, Bitmap fallback)
    {
        if (string.IsNullOrWhiteSpace(iconId) || !_iconPaths.TryGetValue(iconId, out var relativePath))
            return fallback;

        if (_bitmapCache.TryGetValue(iconId, out var cached))
            return cached;

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_themeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
                return fallback;

            using var stream = File.OpenRead(fullPath);
            var bitmap = new Bitmap(stream);
            _bitmapCache[iconId] = bitmap;
            return bitmap;
        }
        catch
        {
            return fallback;
        }
    }

    private static LocalFileIconTheme Empty(Bitmap fallbackFolderIcon, Bitmap fallbackFileIcon)
    {
        return new LocalFileIconTheme(
            AppContext.BaseDirectory,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            fallbackFolderIcon,
            fallbackFileIcon);
    }

    private static Dictionary<string, string> ReadIconDefinitions(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("iconDefinitions", out var definitions) ||
            definitions.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var definition in definitions.EnumerateObject())
        {
            if (definition.Value.ValueKind != JsonValueKind.Object ||
                !definition.Value.TryGetProperty("iconPath", out var iconPathElement))
                continue;

            var iconPath = iconPathElement.GetString();
            if (!string.IsNullOrWhiteSpace(iconPath))
                result[NormalizeKey(definition.Name)] = iconPath;
        }

        return result;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out var section) ||
            section.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var item in section.EnumerateObject())
        {
            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                result[NormalizeKey(item.Name)] = value;
        }

        return result;
    }

    private static void ApplyLocalOverrides(
        string themeRoot,
        Dictionary<string, string> fileExtensions,
        Dictionary<string, string> fileNames,
        Dictionary<string, string> folderNames,
        Dictionary<string, string> folderNamesExpanded,
        Dictionary<string, string> rootFolderNames,
        Dictionary<string, string> rootFolderNamesExpanded)
    {
        var overridesPath = Path.Combine(themeRoot, OverridesFileName);
        if (!File.Exists(overridesPath))
            return;

        try
        {
            using var stream = File.OpenRead(overridesPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            MergeStringMap(root, "fileExtensions", fileExtensions);
            MergeStringMap(root, "fileNames", fileNames);
            MergeStringMap(root, "folderNames", folderNames);
            MergeStringMap(root, "folderNamesExpanded", folderNamesExpanded);
            MergeStringMap(root, "rootFolderNames", rootFolderNames);
            MergeStringMap(root, "rootFolderNamesExpanded", rootFolderNamesExpanded);
        }
        catch
        {
            // 本地覆盖文件只是增强映射，格式错误时退回供应商主题。
        }
    }

    private static void MergeStringMap(JsonElement root, string propertyName, Dictionary<string, string> target)
    {
        if (!root.TryGetProperty(propertyName, out var section) ||
            section.ValueKind != JsonValueKind.Object)
            return;

        foreach (var item in section.EnumerateObject())
        {
            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                target[NormalizeKey(item.Name)] = NormalizeKey(value);
        }
    }

    private static bool TryResolveFolderMap(
        Dictionary<string, string> map,
        string folderName,
        string relativePath,
        out string iconId)
    {
        if (map.TryGetValue(relativePath, out iconId!) ||
            map.TryGetValue(folderName, out iconId!))
            return true;

        iconId = string.Empty;
        return false;
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static IEnumerable<string> EnumerateExtensionCandidates(string fileName)
    {
        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join('.', parts.Skip(i));
    }

    private static string NormalizeKey(string value)
    {
        return value.Replace('\\', '/').Trim().ToLowerInvariant();
    }
}
