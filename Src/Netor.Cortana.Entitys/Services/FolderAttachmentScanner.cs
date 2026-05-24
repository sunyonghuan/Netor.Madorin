namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// P3-2：文件夹附件扫描器 — 递归枚举文件夹内容 + 过滤规则 + 大小统计。
/// 用于 Chat/Workflow/GroupChat 拖放文件夹时生成 AttachmentInfo。
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/02-P3方案设计.md §2 P3-2。
/// </summary>
public sealed class FolderAttachmentScanner
{
    /// <summary>
    /// 扫描文件夹，返回有效文件列表、排除原因、总大小和文件数。
    /// </summary>
    /// <param name="folderPath">目标文件夹绝对路径。</param>
    /// <param name="options">扫描配置（排除规则/最大深度/单文件上限）。为 null 时使用默认配置。</param>
    /// <returns>扫描结果。</returns>
    public FolderScanResult Scan(string folderPath, FolderScanOptions? options = null)
    {
        options ??= FolderScanOptions.Default;

        var includedFiles = new List<string>();
        var excludedReasons = new List<string>();
        long totalBytes = 0;

        ScanRecursive(folderPath, folderPath, options, includedFiles, excludedReasons, ref totalBytes, 0);

        return new FolderScanResult(
            RootPath: folderPath,
            IncludedFiles: includedFiles,
            ExcludedReasons: excludedReasons,
            TotalBytes: totalBytes,
            FileCount: includedFiles.Count);
    }

    private static void ScanRecursive(
        string rootPath,
        string currentPath,
        FolderScanOptions options,
        List<string> includedFiles,
        List<string> excludedReasons,
        ref long totalBytes,
        int depth)
    {
        if (depth > options.MaxDepth)
        {
            excludedReasons.Add($"超过最大深度 {options.MaxDepth}：{currentPath}");
            return;
        }

        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(currentPath);
        }
        catch (Exception ex)
        {
            excludedReasons.Add($"无法访问目录：{currentPath}（{ex.Message}）");
            return;
        }

        // 检查目录名是否匹配排除规则
        if (depth > 0 && IsExcludedDirectory(dirInfo.Name, options.ExcludePatterns))
        {
            excludedReasons.Add($"目录被排除：{dirInfo.Name}");
            return;
        }

        // 枚举文件
        FileInfo[] files;
        try
        {
            files = dirInfo.GetFiles();
        }
        catch (Exception ex)
        {
            excludedReasons.Add($"无法枚举文件：{currentPath}（{ex.Message}）");
            files = [];
        }

        foreach (var file in files)
        {
            // 检查文件扩展名是否匹配排除规则
            if (IsExcludedFile(file.Name, file.Extension, options.ExcludePatterns))
            {
                excludedReasons.Add($"文件被排除：{file.Name}");
                continue;
            }

            // 检查文件大小
            if (file.Length > options.MaxFileBytes)
            {
                excludedReasons.Add($"文件过大 ({file.Length / 1024 / 1024}MB)：{file.Name}");
                continue;
            }

            includedFiles.Add(file.FullName);
            totalBytes += file.Length;
        }

        // 递归子目录
        DirectoryInfo[] subDirs;
        try
        {
            subDirs = dirInfo.GetDirectories();
        }
        catch (Exception ex)
        {
            excludedReasons.Add($"无法枚举子目录：{currentPath}（{ex.Message}）");
            return;
        }

        foreach (var subDir in subDirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            ScanRecursive(rootPath, subDir.FullName, options, includedFiles, excludedReasons, ref totalBytes, depth + 1);
        }
    }

    /// <summary>检查目录名是否匹配排除规则。</summary>
    private static bool IsExcludedDirectory(string dirName, IReadOnlyList<string> excludePatterns)
    {
        foreach (var pattern in excludePatterns)
        {
            // 不含 * 和 . 开头的 pattern 视为目录名完整匹配
            if (!pattern.Contains('*') && !pattern.Contains('.'))
            {
                if (string.Equals(dirName, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 以 . 开头的 pattern 视为隐藏目录匹配
            if (pattern.StartsWith('.') && !pattern.Contains('*'))
            {
                if (string.Equals(dirName, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // 隐藏目录（以 . 开头）始终排除
        if (dirName.StartsWith('.'))
            return true;

        return false;
    }

    /// <summary>检查文件名/扩展名是否匹配排除规则。</summary>
    private static bool IsExcludedFile(string fileName, string extension, IReadOnlyList<string> excludePatterns)
    {
        foreach (var pattern in excludePatterns)
        {
            // *.ext 格式：匹配扩展名
            if (pattern.StartsWith("*."))
            {
                var ext = pattern[1..]; // ".ext"
                if (string.Equals(extension, ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // 隐藏文件（以 . 开头）
        if (fileName.StartsWith('.'))
            return true;

        return false;
    }
}

/// <summary>
/// 文件夹扫描结果。
/// </summary>
/// <param name="RootPath">扫描的根文件夹路径。</param>
/// <param name="IncludedFiles">通过过滤的文件绝对路径列表。</param>
/// <param name="ExcludedReasons">被排除的原因列表（用于调试）。</param>
/// <param name="TotalBytes">有效文件总大小（字节）。</param>
/// <param name="FileCount">有效文件数。</param>
public sealed record FolderScanResult(
    string RootPath,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<string> ExcludedReasons,
    long TotalBytes,
    int FileCount);

/// <summary>
/// 文件夹扫描配置。
/// </summary>
public sealed class FolderScanOptions
{
    /// <summary>默认实例（懒创建，避免每次 new）。</summary>
    public static FolderScanOptions Default { get; } = new();

    /// <summary>
    /// 排除规则列表。
    /// - 不含 * 且不含 . 开头的：视为目录名完整匹配（如 "node_modules", "bin"）
    /// - 以 . 开头的：视为隐藏目录/文件匹配（如 ".git", ".vs"）
    /// - *.ext 格式：视为扩展名匹配（如 "*.dll", "*.exe"）
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } =
    [
        "node_modules", ".git", "bin", "obj", "dist", ".vs",
        ".idea", "__pycache__", ".venv", "venv",
        "*.dll", "*.exe", "*.pdb", "*.so", "*.dylib",
        "*.zip", "*.tar.gz", "*.7z", "*.rar",
        "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.ico",
        "*.mp3", "*.mp4", "*.avi", "*.mov",
    ];

    /// <summary>最大递归层数（防止符号链接循环）。</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>单文件最大大小（超过自动过滤）。默认 10MB。</summary>
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;
}
