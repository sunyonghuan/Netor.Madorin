using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Entitys;
using ProcessDiag = System.Diagnostics.Process;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件浏览器 - 专业的文件操作工具，带有安全约束
/// </summary>
public sealed class FileBrowser
{
    private const string WorkspaceBoundaryViolationMessage = "当前路径超出工作目录边界。如需访问工作目录外的路径，请先提醒用户并获得用户明确同意。";

    private readonly IAppPaths _appPaths;
    private readonly ILogger<FileBrowser> _logger;

    // 约束常量
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB
    private const int MaxListItems = 100;

    // 系统文件夹黑名单
    private static readonly HashSet<string> SystemFoldersBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "C:\\Windows",
        "C:\\Program Files",
        "C:\\Program Files (x86)",
        "C:\\ProgramData",
        "C:\\System Volume Information",
        "C:\\$Recycle.Bin"
    };

    // 允许的文件扩展名
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文本文件
        ".txt", ".md", ".log", ".csv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".ini", ".conf", ".toml", ".env", ".properties", ".rtf", ".code-workspace", ".editorconfig", ".gitignore", ".gitattributes", ".dockerignore", ".gitmodules", ".githubignore", ".coveragerc", ".yamllint", ".npmrc", ".yarnrc", ".clang-format", ".clang-tidy", ".npmignore", ".prettierignore", ".prettierrc", ".eslintrc", ".git-blame-ignore-revs", ".eslintignore", ".stylelintrc", ".stylelintignore", ".babelrc", ".browserslistrc", ".nvmrc",
        // 代码文件
        ".cs", ".csx", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".proj", ".sln", ".slnx", ".cmd", ".bat", ".java", ".js", ".ts", ".tsx", ".jsx", ".py", ".cpp", ".c", ".h", ".go", ".rs", ".rb", ".php", ".html", ".css", ".sql", ".sh", ".ps1", ".psm1", ".psd1", ".scala", ".kt", ".swift", ".m", ".lua", ".r", ".gradle", ".vue", ".nuspec",
        // 图片文件
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff", ".webp",
        // 视频文件
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".3gp", ".ogv",
        // 音频文件
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff",
        // 压缩文件
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso",
        // 文档
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub", ".mobi",
        // 数据库和二进制
        ".sqlite", ".db", ".mdb", ".jar", ".exe", ".dll", ".so", ".dylib"
    };

    public FileBrowser(IAppPaths appPaths, ILogger<FileBrowser> logger)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// 列出目录内容
    /// </summary>
    public DirectoryBrowseResult ListDirectory(string path, bool recursive = false, int maxItems = MaxListItems)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            // 验证路径安全性
            if (!IsPathSafe(fullPath))
                return CreateErrorResult(path, WorkspaceBoundaryViolationMessage);

            var dirInfo = new DirectoryInfo(fullPath);
            if (!dirInfo.Exists)
                return CreateErrorResult(path, "目录不存在");

            var result = new DirectoryBrowseResult { Path = fullPath, LimitCount = maxItems };
            var items = new List<FileItemInfo>();

            // 列出子项
            try
            {
                var entries = dirInfo.GetDirectories().Cast<FileSystemInfo>().Concat(dirInfo.GetFiles()).ToArray();

                foreach (var entry in entries.Take(maxItems))
                {
                    try
                    {
                        var fileInfo = GetFileItemInfo(entry.FullName);
                        if (fileInfo != null)
                        {
                            items.Add(fileInfo);
                            if (fileInfo.Type == "file")
                                result.TotalFiles++;
                            else
                                result.TotalFolders++;
                        }
                    }
                    catch { }
                }

                result.HasMore = entries.Length > maxItems;
            }
            catch (UnauthorizedAccessException)
            {
                return CreateErrorResult(path, "没有访问权限");
            }

            result.Items = items;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出目录失败: {Path}", path);
            return CreateErrorResult(path, ex.Message);
        }
    }

    /// <summary>
    /// 获取文件信息
    /// </summary>
    public FileItemInfo? GetFileInfo(string path)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (!IsPathSafe(fullPath))
            {
                _logger.LogWarning("不允许访问: {Path}", path);
                return null;
            }

            return GetFileItemInfo(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取文件信息失败: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// 读取文件内容
    /// </summary>
    internal TextFileReadResult ReadFileContent(string path, int? startLine = null, int? endLine = null, bool withLineNumbers = true)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            // 验证路径
            if (!IsPathSafe(fullPath))
                return TextFileReadResult.CreateError(path, WorkspaceBoundaryViolationMessage);

            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
                return TextFileReadResult.CreateError(path, "文件不存在");

            // 检查文件类型
            if (!IsAllowedFileType(fileInfo.Extension))
                return TextFileReadResult.CreateError(path, $"不支持此文件类型 ({fileInfo.Extension})");

            // 检查文件大小
            if (fileInfo.Length > MaxFileSize)
                return TextFileReadResult.CreateError(path, $"文件过大 ({FormatFileSize(fileInfo.Length)})，超过限制 ({FormatFileSize(MaxFileSize)})");

            if (startLine is null && endLine is not null)
                return TextFileReadResult.CreateError(path, "仅提供 endLine 无效，请同时提供 startLine");

            var document = TextFileDocument.Load(fullPath);

            var resolvedStartLine = startLine ?? (document.TotalLines > 0 ? 1 : 0);
            var resolvedEndLine = startLine is null
                ? document.TotalLines
                : endLine ?? startLine.Value;

            if (document.TotalLines == 0)
            {
                if (startLine is not null)
                    return TextFileReadResult.CreateError(path, "文件为空，没有可读取的行");

                return TextFileReadResult.CreateSuccess(
                    path: fullPath,
                    totalLines: 0,
                    startLine: 0,
                    endLine: 0,
                    withLineNumbers: withLineNumbers,
                    encoding: document.EncodingDisplayName,
                    newLine: document.NewLineDisplayName,
                    hash: document.Hash,
                    lines: []);
            }

            if (resolvedStartLine < 1 || resolvedStartLine > document.TotalLines)
            {
                return TextFileReadResult.CreateError(
                    path,
                    $"startLine 越界：{resolvedStartLine}，有效范围为 1 到 {document.TotalLines}");
            }

            if (resolvedEndLine < resolvedStartLine || resolvedEndLine > document.TotalLines)
            {
                return TextFileReadResult.CreateError(
                    path,
                    $"endLine 越界：{resolvedEndLine}，有效范围为 {resolvedStartLine} 到 {document.TotalLines}");
            }

            var lines = document.Lines
                .Skip(resolvedStartLine - 1)
                .Take(resolvedEndLine - resolvedStartLine + 1)
                .Select((line, index) => new TextFileLine(resolvedStartLine + index, line))
                .ToList();

            return TextFileReadResult.CreateSuccess(
                path: fullPath,
                totalLines: document.TotalLines,
                startLine: resolvedStartLine,
                endLine: resolvedEndLine,
                withLineNumbers: withLineNumbers,
                encoding: document.EncodingDisplayName,
                newLine: document.NewLineDisplayName,
                hash: document.Hash,
                lines: lines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败: {Path}", path);
            return TextFileReadResult.CreateError(path, ex.Message);
        }
    }

    /// <summary>
    /// 搜索文件
    /// </summary>
    public async Task<FileSearchResult> SearchFilesAsync(
        string rootPath,
        string pattern,
        bool recursive = true,
        int maxResults = 50,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new FileSearchResult { RootPath = rootPath, Pattern = pattern };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var fullRootPath = ResolveSafePath(rootPath);

            // 验证路径
            if (!IsPathSafe(fullRootPath))
            {
                result.Files.Add(new FileItemInfo { Name = "错误", FullPath = WorkspaceBoundaryViolationMessage });
                return result;
            }

            result.RootPath = fullRootPath;

            var rootDir = new DirectoryInfo(fullRootPath);
            if (!rootDir.Exists)
            {
                result.Files.Add(new FileItemInfo { Name = "错误", FullPath = "目录不存在" });
                return result;
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = new List<FileInfo>();
            var foundCount = 0;

            // 异步搜索
            await Task.Run(() =>
            {
                try
                {
                    var fileEnumerator = rootDir.EnumerateFiles(pattern, searchOption).GetEnumerator();

                    while (fileEnumerator.MoveNext() && foundCount < maxResults && !ct.IsCancellationRequested)
                    {
                        var fileInfo = fileEnumerator.Current;

                        // 检查文件类型和大小
                        if (IsAllowedFileType(fileInfo.Extension) && fileInfo.Length <= MaxFileSize)
                        {
                            files.Add(fileInfo);
                            foundCount++;
                            progress?.Report(foundCount);
                        }
                    }
                }
                catch { }
            }, ct);

            // 转换为结果
            foreach (var file in files)
            {
                var itemInfo = GetFileItemInfo(file.FullName);
                if (itemInfo != null)
                {
                    result.Files.Add(itemInfo);
                }
            }

            result.IsCompleted = true;
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;

            _logger.LogInformation("搜索完成: {RootPath} 模式: {Pattern} 找到 {Count} 个文件 ({ElapsedMs}ms)",
                fullRootPath, pattern, result.Files.Count, result.ElapsedMs);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索文件失败: {RootPath} 模式: {Pattern}", rootPath, pattern);
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.IsCompleted = false;
            return result;
        }
    }

    /// <summary>
    /// 获取所有驱动器
    /// </summary>
    public List<string> GetAvailableDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取驱动器列表失败");
            return new List<string>();
        }
    }

    /// <summary>
    /// 在资源管理器中打开或定位指定路径。
    /// </summary>
    public string OpenInExplorer(string path, string mode = "open")
    {
        if (string.IsNullOrWhiteSpace(path))
            return "错误：路径不能为空";

        if (!OperatingSystem.IsWindows())
            return "错误：当前系统不支持资源管理器操作";

        try
        {
            var fullPath = ResolveSafePath(path);
            if (!IsPathSafe(fullPath))
                return $"错误：{WorkspaceBoundaryViolationMessage}";

            var normalizedMode = string.IsNullOrWhiteSpace(mode)
                ? "open"
                : mode.Trim().ToLowerInvariant();

            var fileExists = File.Exists(fullPath);
            var directoryExists = Directory.Exists(fullPath);

            string arguments;
            string actionName;

            switch (normalizedMode)
            {
                case "open":
                    if (!directoryExists)
                        return "错误：目录不存在";

                    arguments = $"\"{fullPath}\"";
                    actionName = "打开文件夹";
                    break;

                case "select":
                    if (!fileExists && !directoryExists)
                        return "错误：文件或文件夹不存在";

                    arguments = $"/select,\"{fullPath}\"";
                    actionName = fileExists ? "定位文件" : "定位目录";
                    break;

                default:
                    return $"错误：不支持的模式 '{mode}'，仅支持 open 或 select";
            }

            var process = ProcessDiag.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });

            if (process == null)
            {
                _logger.LogError("启动资源管理器失败: {Path} Mode: {Mode}", fullPath, normalizedMode);
                return "错误：无法启动资源管理器";
            }

            _logger.LogInformation("资源管理器操作成功: {ActionName} {Path}", actionName, fullPath);
            return $"✓ 已在资源管理器中{actionName}：{fullPath}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "资源管理器操作失败: {Path} Mode: {Mode}", path, mode);
            return $"错误：{ex.Message}";
        }
    }

    /// <summary>
    /// 路径是否安全
    /// </summary>
    private bool IsPathSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var workspace = Path.GetFullPath(_appPaths.WorkspaceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 规范化路径
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(workspace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 检查黑名单
        foreach (var blacklisted in SystemFoldersBlacklist)
        {
            if (fullPath.StartsWith(blacklisted, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private string ResolveSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空", nameof(path));

        var workspace = Path.GetFullPath(_appPaths.WorkspaceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspace, path));

        return fullPath;
    }

    /// <summary>
    /// 检查文件类型是否允许
    /// </summary>
    private bool IsAllowedFileType(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return AllowedExtensions.Contains(extension);
    }

    /// <summary>
    /// 获取文件类型分类
    /// </summary>
    private AllowedFileType? GetFileTypeCategory(string extension)
    {
        return extension.ToLower() switch
        {
            ".txt" or ".md" or ".log" or ".csv" or ".json" or ".jsonc" or ".xml" or ".yaml" or ".yml" or ".ini" or ".conf" or ".toml" or ".env" or ".properties" or ".rtf" or ".code-workspace" or ".editorconfig" or ".gitignore" or ".gitattributes" or ".dockerignore" or ".gitmodules" or ".githubignore" or ".coveragerc" or ".yamllint" or ".npmrc" or ".yarnrc" or ".clang-format" or ".clang-tidy" or ".npmignore" or ".prettierignore" or ".prettierrc" or ".eslintrc" or ".git-blame-ignore-revs" or ".eslintignore" or ".stylelintrc" or ".stylelintignore" or ".babelrc" or ".browserslistrc" or ".nvmrc"
                => AllowedFileType.Text,

            ".cs" or ".csx" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".proj" or ".sln" or ".slnx" or ".cmd" or ".bat" or ".java" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".cpp" or ".c" or ".h" or ".go" or ".rs" or ".rb" or ".php" or ".html" or ".css" or ".sql" or ".sh" or ".ps1" or ".psm1" or ".psd1" or ".scala" or ".kt" or ".swift" or ".m" or ".lua" or ".r" or ".gradle" or ".vue" or ".nuspec"
                => AllowedFileType.Code,

            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" or ".webp" or ".ico" or ".tiff"
                => AllowedFileType.Image,

            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".3gp" or ".ogv"
                => AllowedFileType.Video,

            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".wma" or ".opus" or ".aiff"
                => AllowedFileType.Audio,

            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso"
                => AllowedFileType.Compressed,

            _ => null
        };
    }

    /// <summary>
    /// 获取文件项信息
    /// </summary>
    private FileItemInfo? GetFileItemInfo(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                var extension = fileInfo.Extension;

                return new FileItemInfo
                {
                    Name = fileInfo.Name,
                    FullPath = fileInfo.FullName,
                    Type = "file",
                    Size = fileInfo.Length,
                    SizeFormatted = FormatFileSize(fileInfo.Length),
                    Extension = extension,
                    Created = fileInfo.CreationTime,
                    Modified = fileInfo.LastWriteTime,
                    Accessed = fileInfo.LastAccessTime,
                    IsReadOnly = fileInfo.IsReadOnly,
                    IsHidden = (fileInfo.Attributes & FileAttributes.Hidden) != 0,
                    FileTypeCategory = GetFileTypeCategory(extension)
                };
            }
            else if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                var itemCount = 0;

                try
                {
                    itemCount = dirInfo.GetDirectories().Length + dirInfo.GetFiles().Length;
                }
                catch { }

                return new FileItemInfo
                {
                    Name = dirInfo.Name,
                    FullPath = dirInfo.FullName,
                    Type = "folder",
                    Size = 0,
                    SizeFormatted = "-",
                    Created = dirInfo.CreationTime,
                    Modified = dirInfo.LastWriteTime,
                    Accessed = dirInfo.LastAccessTime,
                    IsReadOnly = (dirInfo.Attributes & FileAttributes.ReadOnly) != 0,
                    IsHidden = (dirInfo.Attributes & FileAttributes.Hidden) != 0,
                    ItemCount = itemCount
                };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 格式化文件大小
    /// </summary>
    private string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 创建错误结果
    /// </summary>
    private DirectoryBrowseResult CreateErrorResult(string path, string error)
    {
        return new DirectoryBrowseResult
        {
            Path = path,
            Items = new List<FileItemInfo>
            {
                new FileItemInfo { Name = "错误", FullPath = error }
            }
        };
    }
}
