namespace Netor.Cortana.UI.Controls.Shared.Internal;

/// <summary>
/// 文件树的路径 → 节点索引。用于把「按路径找节点」从 DFS 降到 O(1)。
///
/// 契约：
/// - key 采用 <see cref="Normalize"/> 归一化（去尾分隔符、比较大小写不敏感）；
/// - 只跟踪已装载进 UI 的节点（未展开的子孙不会出现在索引里）；
/// - 生命周期由 <see cref="WorkspaceExplorer"/> 独占，非线程安全，仅 UI 线程访问。
/// </summary>
internal sealed class FileTreePathIndex
{
    private readonly Dictionary<string, FileTreeNode> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;

    public void Clear() => _map.Clear();

    public bool Contains(string fullPath)
    {
        return _map.ContainsKey(Normalize(fullPath));
    }

    public bool TryGet(string fullPath, out FileTreeNode node)
    {
        return _map.TryGetValue(Normalize(fullPath), out node!);
    }

    public void Add(FileTreeNode node)
    {
        if (node.IsPlaceholder) return;
        _map[Normalize(node.FullPath)] = node;
    }

    /// <summary>
    /// 从索引移除单个节点（不递归）。
    /// </summary>
    public void Remove(FileTreeNode node)
    {
        _map.Remove(Normalize(node.FullPath));
    }

    /// <summary>
    /// 递归移除节点及其所有已装载后代。
    /// </summary>
    public void RemoveRecursive(FileTreeNode node)
    {
        _map.Remove(Normalize(node.FullPath));
        if (node.Children.Count == 0) return;

        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder) continue;
            RemoveRecursive(child);
        }
    }

    /// <summary>
    /// 枚举当前索引中所有「已展开」的目录节点归一化路径。
    /// 用于刷新/溢出重载前快照展开态，重载后据此恢复。
    /// </summary>
    public IReadOnlyCollection<string> EnumerateExpandedDirectoryPaths()
    {
        var result = new List<string>();
        foreach (var node in _map.Values)
        {
            if (node.IsDirectory && node.IsExpanded)
                result.Add(Normalize(node.FullPath));
        }

        return result;
    }

    /// <summary>
    /// 路径规范化：统一去掉尾部分隔符。大小写通过 <see cref="StringComparer.OrdinalIgnoreCase"/> 处理。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
