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

using Netor.Cortana.UI.Controls.Shared.Internal;

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
    /// 路径 → 节点 O(1) 索引，替代 DFS 遍历（<see cref="FileTreePathIndex"/>）。
    /// 仅记录已装载进 UI 的节点；工作区切换/子树重建时同步维护。
    /// </summary>
    private readonly FileTreePathIndex _pathIndex = new();

    /// <summary>
    /// FSW 事件 200ms 去抖 + 合并批处理器。避免事件风暴期同步刷新打爆 UI 线程。
    /// </summary>
    private readonly WatcherEventBatcher _batcher;

    /// <summary>
    /// 工作区装载代号。每次全量装载递增；异步装载 await 恢复后凡代号不符即视为被抢占，丢弃结果。
    /// </summary>
    private int _workspaceGeneration;

    /// <summary>
    /// 刷新/溢出重载时待恢复的展开目录路径集合（<see cref="StringComparer.OrdinalIgnoreCase"/>）。
    /// 命中即在装载后自动展开，触发下一级懒加载，逐级把展开态还原回去。
    /// </summary>
    private readonly HashSet<string> _expansionToRestore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="_expansionToRestore"/> 生效的装载代号。仅当与当前 <see cref="_workspaceGeneration"/>
    /// 相符时才使用，避免跨工作区切换后拿旧展开态误展开新树。
    /// </summary>
    private int _expansionRestoreGeneration = -1;

    /// <summary>
    /// 重载后待恢复的选中节点路径。目标节点随懒加载逐级出现，命中即选中并清空。
    /// </summary>
    private string? _selectionToRestore;

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
    /// 工作目录变更事件的订阅标识，用于控件卸载时退订，避免事件泄漏。
    /// </summary>
    private readonly Guid _workspaceChangedSubscriptionId;

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
        _batcher = new WatcherEventBatcher(HandleWatcherDrain);
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _workspaceChangedSubscriptionId = _subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, args) =>
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
            _ = LoadWorkspaceAsync();
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

    /// <summary>
    /// 目录枚举的轻量结果项。仅承载磁盘 I/O 能拿到的原始信息（路径 / 名称 / 是否目录），
    /// 不含图标——图标解析走非线程安全的位图缓存，必须留到 UI 线程 <see cref="CreateNode"/> 时做。
    /// </summary>
    private readonly record struct DirEntry(string FullPath, string Name, bool IsDirectory);

    /// <summary>
    /// 异步装载整个工作区：只枚举根目录一级，子级留到展开时懒加载。
    /// 枚举 I/O 放到线程池，节点构建 + 索引回到 UI 线程；用 <see cref="_workspaceGeneration"/>
    /// 丢弃被后续工作区切换抢占的过期装载。
    /// </summary>
    private async Task LoadWorkspaceAsync()
    {
        // 每次装载递增代号：await 恢复后凡代号不符即视为被抢占，直接丢弃结果。
        var generation = ++_workspaceGeneration;

        _batcher.Clear();
        TreeNodes.Clear();
        _pathIndex.Clear();
        DisposeWatcher();

        if (string.IsNullOrEmpty(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        // 先起监视器再枚举：装载期间的增删事件先进 batcher 排队，装载完 FlushNow 一次性消化。
        StartWatcher();

        var root = _workspaceDirectory;
        List<DirEntry> entries;
        try
        {
            entries = await Task.Run(() => EnumerateLevel(root, isRootLevel: true));
        }
        catch
        {
            return;
        }

        if (generation != _workspaceGeneration)
            return; // 已被更晚的工作区切换抢占

        foreach (var entry in entries)
        {
            var node = CreateNode(entry.FullPath, entry.Name, entry.IsDirectory, parent: null, depth: 0);
            TreeNodes.Add(node);
            _pathIndex.Add(node);
        }

        MarkLastChild(TreeNodes);
        RestoreExpansion(TreeNodes);
        TryRestoreSelection();
        ScheduleTreeConnectorUpdate();

        // 消化装载期间积累的文件系统事件。
        _batcher.FlushNow();
    }

    /// <summary>
    /// 刷新/溢出重载：先快照当前展开目录 + 选中节点，再全量重载并逐级还原展开态与选中。
    /// 展开态快照只取「已 Loaded 且 IsExpanded」的目录路径，用生成代号绑定本次重载，
    /// 避免跨工作区切换误用旧展开态。
    /// </summary>
    private Task ReloadPreservingExpansionAsync()
    {
        _expansionToRestore.Clear();
        foreach (var path in _pathIndex.EnumerateExpandedDirectoryPaths())
            _expansionToRestore.Add(path);
        _expansionRestoreGeneration = _workspaceGeneration + 1; // 下一次装载的代号
        _selectionToRestore = (FileTree.SelectedItem as FileTreeNode)?.FullPath;

        return LoadWorkspaceAsync();
    }

    /// <summary>
    /// 在指定集合中把命中 <see cref="_expansionToRestore"/> 的目录节点置展开，
    /// 触发下一级懒加载，从而逐级把展开态还原下去。仅当恢复代号与当前装载代号相符时生效。
    /// </summary>
    private void RestoreExpansion(IEnumerable<FileTreeNode> nodes)
    {
        if (_expansionToRestore.Count == 0 || _expansionRestoreGeneration != _workspaceGeneration)
            return;

        foreach (var node in nodes)
        {
            if (node.IsDirectory && _expansionToRestore.Contains(node.FullPath))
                node.IsExpanded = true; // 触发 OnNodePropertyChanged → LoadChildrenAsync
        }
    }

    /// <summary>
    /// 若待恢复的选中节点已随懒加载出现在索引中，则选中它并清空待恢复标记。
    /// </summary>
    private void TryRestoreSelection()
    {
        if (_selectionToRestore is null)
            return;
        if (_pathIndex.TryGet(_selectionToRestore, out var target))
        {
            FileTree.SelectedItem = target;
            _selectionToRestore = null;
        }
    }

    /// <summary>
    /// 首次展开某目录节点时装载其直接子级（同样只装一级）。
    /// 竞态处理：per-node <see cref="FileTreeNode.LoadCts"/> 取消上一次未完成装载，
    /// <see cref="_workspaceGeneration"/> 丢弃跨工作区的过期结果，装载前复核节点仍在树中。
    /// </summary>
    private async Task LoadChildrenAsync(FileTreeNode node)
    {
        if (node.LoadState is FileTreeLoadState.Loading or FileTreeLoadState.Loaded)
            return;

        node.LoadState = FileTreeLoadState.Loading;
        var generation = _workspaceGeneration;

        node.LoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        node.LoadCts = cts;
        var token = cts.Token;

        var path = node.FullPath;
        List<DirEntry> entries;
        try
        {
            entries = await Task.Run(() => EnumerateLevel(path, isRootLevel: false), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            node.LoadState = FileTreeLoadState.Failed;
            return;
        }

        // 回到 UI 线程后复核：装载未被取消、未跨工作区、节点仍在索引中。
        if (token.IsCancellationRequested || generation != _workspaceGeneration || !_pathIndex.Contains(node.FullPath))
            return;

        // 清掉占位子节点（及可能的历史子节点），换成真实一级子级。
        foreach (var child in node.Children)
        {
            if (!child.IsPlaceholder)
                _pathIndex.RemoveRecursive(child);
        }
        node.Children.Clear();

        foreach (var entry in entries)
        {
            var childNode = CreateNode(entry.FullPath, entry.Name, entry.IsDirectory, node, node.Depth + 1);
            node.Children.Add(childNode);
            _pathIndex.Add(childNode);
        }

        MarkLastChild(node.Children);
        node.LoadState = FileTreeLoadState.Loaded;

        // 逐级还原展开态与选中：本级新出的子目录若在待恢复集里，展开触发下一级装载。
        RestoreExpansion(node.Children);
        TryRestoreSelection();
        ScheduleTreeConnectorUpdate();
    }

    /// <summary>
    /// 枚举单层目录（不递归）。纯磁盘 I/O，可在线程池执行；命中忽略集的目录直接跳过。
    /// 排序规则与旧实现一致：文件夹在前、同类按名称忽略大小写升序。
    /// </summary>
    private static List<DirEntry> EnumerateLevel(string path, bool isRootLevel)
    {
        var result = new List<DirEntry>();

        try
        {
            var dirInfo = new DirectoryInfo(path);

            var dirs = dirInfo.GetDirectories()
                .Where(d => !WorkspaceIgnoreRules.ShouldIgnoreName(d.Name, isRootLevel))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
                result.Add(new DirEntry(dir.FullName, dir.Name, IsDirectory: true));

            var files = dirInfo.GetFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                result.Add(new DirEntry(file.FullName, file.Name, IsDirectory: false));
        }
        catch
        {
            // 权限不足等情况静默忽略
        }

        return result;
    }

    /// <summary>
    /// 在 UI 线程由原始信息构建单个节点，解析图标。目录节点挂上展开监听 + 占位子节点
    /// （保证折叠状态下展开箭头可见，且首次展开触发 <see cref="LoadChildrenAsync"/>）。
    /// </summary>
    private FileTreeNode CreateNode(string fullPath, string name, bool isDirectory, FileTreeNode? parent, int depth)
    {
        var relativePath = GetRelativeThemePath(_workspaceDirectory, fullPath);

        if (isDirectory)
        {
            var node = new FileTreeNode
            {
                Kind = FileTreeNodeKind.Directory,
                Name = name,
                FullPath = fullPath,
                ClosedIcon = FileIconTheme.ResolveFolderIcon(name, relativePath, expanded: false, isRootLevel: depth == 0),
                ExpandedIcon = FileIconTheme.ResolveFolderIcon(name, relativePath, expanded: true, isRootLevel: depth == 0),
                Parent = parent,
                Depth = depth
            };
            AttachDirectoryNode(node);
            return node;
        }

        return new FileTreeNode
        {
            Kind = FileTreeNodeKind.File,
            Name = name,
            FullPath = fullPath,
            ClosedIcon = FileIconTheme.ResolveFileIcon(name, relativePath),
            Parent = parent,
            Depth = depth
        };
    }

    /// <summary>
    /// 给目录节点挂上展开监听并预置占位子节点：占位让 TreeView 渲染出展开箭头，
    /// 首次 <see cref="FileTreeNode.IsExpanded"/> 置真时 <see cref="OnNodePropertyChanged"/> 触发懒加载。
    /// </summary>
    private void AttachDirectoryNode(FileTreeNode node)
    {
        node.PropertyChanged += OnNodePropertyChanged;
        node.Children.Add(CreatePlaceholder(node));
    }

    /// <summary>
    /// 构建一个占位子节点。占位不进 <see cref="_pathIndex"/>，也不参与连接线绘制（Step 8 过滤）。
    /// </summary>
    private static FileTreeNode CreatePlaceholder(FileTreeNode parent)
    {
        return new FileTreeNode
        {
            Kind = FileTreeNodeKind.LoadingPlaceholder,
            Name = string.Empty,
            FullPath = string.Empty,
            ClosedIcon = FileIcon,
            Parent = parent,
            Depth = parent.Depth + 1
        };
    }

    /// <summary>
    /// 目录节点首次展开时触发懒加载。仅关心 <see cref="FileTreeNode.IsExpanded"/> 属性变更。
    /// </summary>
    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileTreeNode.IsExpanded))
            return;
        if (sender is not FileTreeNode node)
            return;
        if (node.IsExpanded && node.LoadState == FileTreeLoadState.NotLoaded)
            _ = LoadChildrenAsync(node);
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

    private static void MarkLastChild(IList<FileTreeNode> nodes)
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

        // 占位节点（懒加载展开箭头用）不参与连接线绘制。
        if (node.IsPlaceholder)
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

        // 节点 → 目标的引用索引，把「找父节点缩进」从 O(n²)（每项 FirstOrDefault 全扫）降到 O(n)。
        var byNode = new Dictionary<FileTreeNode, TreeConnectorTarget>(targets.Count, ReferenceEqualityComparer.Instance);
        foreach (var target in targets)
            byNode[target.Node] = target;

        var indent = targets
            .Where(target => target.Node.Parent is not null)
            .Select(target =>
            {
                return byNode.TryGetValue(target.Node.Parent!, out var parent)
                    ? target.IconLeft - parent.IconLeft
                    : 0;
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
            InternalBufferSize = 65536,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;
        _watcher.Error += OnWatcherError;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // 忽略集在 FSW 回调线程做早期过滤，避免 node_modules 等目录事件进入批处理器。
        if (WorkspaceIgnoreRules.ShouldIgnorePath(_workspaceDirectory, e.FullPath))
            return;

        if (e is RenamedEventArgs renamed)
        {
            if (WorkspaceIgnoreRules.ShouldIgnorePath(_workspaceDirectory, renamed.OldFullPath))
                return;
            _batcher.EnqueueRenamed(renamed.OldFullPath, renamed.FullPath);
            return;
        }

        _batcher.Enqueue(e.ChangeType, e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // FSW 内部缓冲区溢出或其它错误：让 batcher 输出一条 Overflow，触发 Reload。
        _batcher.EnqueueOverflow();
    }

    /// <summary>
    /// batcher drain 出口：把合并去重后的一批动作在 UI 线程逐条精细化应用到树，
    /// 只增删命中的单个节点，绝不整树/整目录重扫（Overflow 例外，此时回退全量重载）。
    /// </summary>
    private void HandleWatcherDrain(IReadOnlyList<WatcherEventRecord> records)
    {
        if (string.IsNullOrWhiteSpace(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
            return;

        // 记住选中节点，rename（删旧+建新）后尽量把选中恢复到新路径。
        var selectedPath = (FileTree.SelectedItem as FileTreeNode)?.FullPath;

        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case WatcherEventKind.Overflow:
                    // FSW 缓冲区溢出，事件已不可靠，只能回退全量重载；保留展开态与选中。
                    _ = ReloadPreservingExpansionAsync();
                    return;
                case WatcherEventKind.Created:
                    ApplyCreated(record.Path);
                    break;
                case WatcherEventKind.Deleted:
                    ApplyDeleted(record.Path);
                    break;
                case WatcherEventKind.Renamed:
                    ApplyRenamed(record.OldPath!, record.Path);
                    break;
            }
        }

        RestoreSelection(selectedPath);
    }

    /// <summary>
    /// 增量插入单个新增节点到正确的父节点下，保持「文件夹在前、同类按名排序」。
    /// batcher drain 已按路径长度升序，父目录先于子入树；父目录不在索引里
    /// （子树尚未装载 / 处于忽略目录内）时静默丢弃，等下次展开或刷新兜底。
    /// </summary>
    private void ApplyCreated(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return;

        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_pathIndex.Contains(normalized))
            return; // 已在树里，去重
        if (WorkspaceIgnoreRules.ShouldIgnorePath(_workspaceDirectory, normalized))
            return;

        var parentDir = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parentDir))
            return;
        var normalizedParent = parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ObservableCollection<FileTreeNode> siblings;
        FileTreeNode? parentNode;
        int depth;

        if (string.Equals(normalizedParent, _workspaceDirectory, StringComparison.OrdinalIgnoreCase))
        {
            siblings = TreeNodes;
            parentNode = null;
            depth = 0;
        }
        else
        {
            if (!_pathIndex.TryGet(normalizedParent, out parentNode) || !parentNode.IsDirectory)
                return; // 父目录未装载/不可见，交给展开或刷新时兜底

            // 父目录尚未展开装载（仍是占位）：跳过，待展开时一次性装全，避免占位与真实子节点混存。
            if (parentNode.LoadState != FileTreeLoadState.Loaded)
                return;

            siblings = parentNode.Children;
            depth = parentNode.Depth + 1;
        }

        // 只判断磁盘上的实际类型；目录节点由 CreateNode 挂占位子节点保持懒加载。
        var isDir = Directory.Exists(normalized);
        if (!isDir && !File.Exists(normalized))
            return; // 建节点前对象已消失

        var node = CreateNode(normalized, Path.GetFileName(normalized), isDir, parentNode, depth);
        InsertSorted(siblings, node);
        _pathIndex.Add(node);
        MarkLastChild(siblings);
        ScheduleTreeConnectorUpdate();
    }

    /// <summary>
    /// 增量删除单个节点及其整棵子树，只从其父集合里摘掉，绝不重扫兄弟。
    /// </summary>
    private void ApplyDeleted(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return;

        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_pathIndex.TryGet(normalized, out var node))
            return; // 不在树里，无需处理

        var siblings = node.Parent?.Children ?? TreeNodes;
        siblings.Remove(node);
        _pathIndex.RemoveRecursive(node);
        MarkLastChild(siblings);
        ScheduleTreeConnectorUpdate();
    }

    /// <summary>
    /// 重命名/移动按「删旧路径 + 建新路径」处理：目录重命名会让所有子孙 FullPath 变化，
    /// 只有重建才能保证子孙路径与索引全部正确；也天然覆盖跨目录移动。
    /// </summary>
    private void ApplyRenamed(string oldPath, string newPath)
    {
        ApplyDeleted(oldPath);
        ApplyCreated(newPath);
    }

    /// <summary>
    /// 把节点插入到有序兄弟集合中的正确位置：文件夹在前、同类按名称 <see cref="StringComparer.OrdinalIgnoreCase"/> 升序。
    /// </summary>
    private static void InsertSorted(ObservableCollection<FileTreeNode> siblings, FileTreeNode node)
    {
        var index = 0;
        while (index < siblings.Count && CompareNodes(node, siblings[index]) >= 0)
            index++;

        siblings.Insert(index, node);
    }

    /// <summary>
    /// 兄弟节点排序比较：文件夹优先，其次按名称忽略大小写。
    /// </summary>
    private static int CompareNodes(FileTreeNode a, FileTreeNode b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreSelection(string? selectedPath)
    {
        if (selectedPath is null) return;
        if (_pathIndex.TryGet(selectedPath, out var target))
            FileTree.SelectedItem = target;
    }

    private void DisposeWatcher()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnFileSystemChanged;
        _watcher.Deleted -= OnFileSystemChanged;
        _watcher.Renamed -= OnFileSystemChanged;
        _watcher.Error -= OnWatcherError;
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
        // 刷新：全量重载但保留当前展开态与选中（用户选择「保留展开态」）。
        _ = ReloadPreservingExpansionAsync();
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
            // 占位节点（加载中）不是真实文件/目录，排除在所有右键操作之外。
            if (item is FileTreeNode { IsPlaceholder: false } node)
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
        // 占位节点无真实路径，拖放不应把它当作落点。
        return item?.DataContext is FileTreeNode { IsPlaceholder: false } node ? node : null;
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
        _batcher.Clear();
        _subscriber.Unsubscribe(_workspaceChangedSubscriptionId);
        DetachNodeHandlers(TreeNodes);
        _pathIndex.Clear();
        base.OnUnloaded(e);
    }

    /// <summary>
    /// 递归解除目录节点的属性变更订阅，避免控件卸载后残留事件引用导致内存泄漏。
    /// </summary>
    private void DetachNodeHandlers(IEnumerable<FileTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                node.PropertyChanged -= OnNodePropertyChanged;
                DetachNodeHandlers(node.Children);
            }
        }
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
