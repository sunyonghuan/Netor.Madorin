namespace Netor.Cortana.UI.Controls.Shared.Internal;

/// <summary>
/// 工作区文件树的静态忽略名单。
///
/// 决策：忽略集采用「保守集」——除了三大版本控制目录外，只挡工程构建/缓存类目录，
/// 不覆盖用户自定义规则（如 <c>.gitignore</c>）。这样既能把 node_modules/bin/obj 挡在树外
/// 消灭 10k+ 文件枚举压力，又不会把 <c>.venv</c>/<c>logs</c> 等用户可能想看的目录一并隐藏。
///
/// 大小写：Windows 是主用平台，统一 <see cref="StringComparer.OrdinalIgnoreCase"/>。
/// </summary>
internal static class WorkspaceIgnoreRules
{
    /// <summary>
    /// 任意层级命中即隐藏的目录名集合。
    /// </summary>
    public static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        ".vs",
        "node_modules",
        "bin",
        "obj",
        "__pycache__",
    };

    /// <summary>
    /// 单个目录/文件名是否命中忽略集。<paramref name="isRootLevel"/> 为 <c>true</c> 时，
    /// 额外把所有 <c>.</c> 开头的目录隐藏（保留原实现语义：根层点目录多为平台/工作区设置目录）。
    /// </summary>
    public static bool ShouldIgnoreName(string name, bool isRootLevel)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (isRootLevel && name.StartsWith('.')) return true;
        return IgnoredNames.Contains(name);
    }

    /// <summary>
    /// 检查完整路径任意段是否命中忽略集。用于 FileSystemWatcher 事件早期过滤，
    /// 避免忽略目录内部变动进入事件队列。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录（已 <see cref="string.TrimEnd(char[])"/>）。</param>
    /// <param name="fullPath">要判定的完整路径。</param>
    public static bool ShouldIgnorePath(string workspaceRoot, string fullPath)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(fullPath))
            return false;

        string relative;
        try
        {
            relative = Path.GetRelativePath(workspaceRoot, fullPath);
        }
        catch
        {
            return false;
        }

        // 越出工作区（GetRelativePath 返回原路径或以 ".." 开头）：视为不受工作区忽略集控制。
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (ShouldIgnoreName(segments[i], isRootLevel: i == 0))
                return true;
        }

        return false;
    }
}
