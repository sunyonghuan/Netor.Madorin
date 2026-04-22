using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件浏览 AI 工具提供者
/// </summary>
public sealed class FileBrowserProvider : AIContextProvider
{
    private readonly ILogger<FileBrowserProvider> _logger;
    private readonly FileBrowser _fileBrowser;
    private readonly List<AITool> _tools = [];

    public FileBrowserProvider(ILogger<FileBrowserProvider> logger, FileBrowser fileBrowser)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileBrowser);

        _logger = logger;
        _fileBrowser = fileBrowser;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
        {
            RegisterTools();
        }

        return new ValueTask<AIContext>(new AIContext { Tools = _tools, Instructions = """
    ### File Browser Rules

    - Scope: use these tools only for the current workspace directory.
    - Never browse, read, search, or open paths outside the current workspace directory.
    - If the user wants to access a path outside the workspace, do not use these tools yet. First get explicit user consent to change the workspace directory, then change the workspace directory, then continue.
    - Prefer relative paths when working inside the workspace.
    - Use sys_list_directory first when the target path is uncertain.
    - Read only allowed text/code/document/media/archive file types.
    - Read file size limit: 10 MB.
    - Directory listing limit: 100 items. Search result limit: 50 items.
    - Blocked system folders remain inaccessible.
    """ });
    }

    private void RegisterTools()
    {
        // 工具1：列出目录
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_directory",
            description: "List files and folders in the current workspace directory. Returns up to 100 items.",
            method: ListDirectoryAsync));

        // 工具2：获取文件信息
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_file_info",
            description: "Get metadata for a file or folder in the current workspace directory.",
            method: GetFileInfoAsync));

        // 工具3：读取文件内容
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_read_file",
            description: "Read a text or code file in the current workspace directory. Allowed file types only, up to 10 MB.",
            method: ReadFileAsync));

        // 工具4：搜索文件
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_search_files",
            description: "Search files in the current workspace directory by pattern. Returns up to 50 matches.",
            method: SearchFilesAsync));

        // 工具5：获取驱动器列表
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_drives",
            description: "Get available system drives. Use only when the user explicitly wants to change the workspace directory.",
            method: GetDrivesAsync));

        // 工具6：资源管理器操作
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_open_in_explorer",
            description: "Open or reveal a file or folder in Explorer within the current workspace directory. mode=open opens a folder. mode=select reveals a file or folder.",
            method: OpenInExplorerAsync));
    }

    private async Task<string> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "✗ 错误：路径不能为空";

            var result = _fileBrowser.ListDirectory(path);

            if (result.Items.Count == 0 || result.Items[0].FullPath.StartsWith("错误"))
                return $"✗ {result.Items.FirstOrDefault()?.FullPath ?? "无法访问目录"}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✓ 目录: {path}");
            sb.AppendLine($"文件: {result.TotalFiles} | 文件夹: {result.TotalFolders}");
            if (result.HasMore)
                sb.AppendLine($"(显示前{result.LimitCount}项，还有更多...)");
            sb.AppendLine();

            foreach (var item in result.Items)
            {
                if (item.Type == "folder")
                {
                    sb.AppendLine($"📁 [{item.ItemCount}] {item.Name}/");
                }
                else
                {
                    var icon = item.FileTypeCategory switch
                    {
                        AllowedFileType.Image => "🖼️",
                        AllowedFileType.Video => "🎬",
                        AllowedFileType.Code => "💻",
                        AllowedFileType.Text => "📄",
                        _ => "📋"
                    };

                    sb.AppendLine($"{icon} {item.Name} ({item.SizeFormatted})");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "列出目录失败: {Path}", path);
            return $"✗ 错误：{ex.Message}";
        }
    }

    private async Task<string> GetFileInfoAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "✗ 错误：路径不能为空";

            var fileInfo = _fileBrowser.GetFileInfo(path);

            if (fileInfo == null)
                return "✗ 错误：找不到文件或文件夹";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✓ 文件信息: {fileInfo.Name}");
            sb.AppendLine($"路径: {fileInfo.FullPath}");
            sb.AppendLine($"类型: {fileInfo.Type}");

            if (fileInfo.Type == "file")
            {
                sb.AppendLine($"大小: {fileInfo.SizeFormatted}");
                sb.AppendLine($"扩展名: {fileInfo.Extension}");
                sb.AppendLine($"类别: {fileInfo.FileTypeCategory}");
                sb.AppendLine($"只读: {fileInfo.IsReadOnly}");
            }
            else
            {
                sb.AppendLine($"子项数: {fileInfo.ItemCount}");
            }

            sb.AppendLine($"隐藏: {fileInfo.IsHidden}");
            sb.AppendLine($"创建时间: {fileInfo.Created:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"修改时间: {fileInfo.Modified:yyyy-MM-dd HH:mm:ss}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取文件信息失败: {Path}", path);
            return $"✗ 错误：{ex.Message}";
        }
    }

    private async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "✗ 错误：路径不能为空";

            var content = _fileBrowser.ReadFileContent(path);

            if (content == null || content.StartsWith("错误"))
                return content ?? "✗ 无法读取文件";

            // 限制输出大小（防止过长）
            var maxLength = 50000;
            if (content.Length > maxLength)
            {
                content = content.Substring(0, maxLength) + $"\n\n... (文件过长，只显示前 {maxLength} 字符)";
            }

            return $"✓ 文件内容:\n\n{content}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败: {Path}", path);
            return $"✗ 错误：{ex.Message}";
        }
    }

    private async Task<string> SearchFilesAsync(
        string rootPath,
        string pattern,
        bool recursive = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return "✗ 错误：路径不能为空";

        if (string.IsNullOrWhiteSpace(pattern))
            return "✗ 错误：搜索模式不能为空";

        try
        {
            var progress = new Progress<int>(count => { });
            var result = await _fileBrowser.SearchFilesAsync(rootPath, pattern, recursive, 50, progress, ct);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🔍 搜索: {rootPath} (模式: {pattern})");

            if (!result.IsCompleted)
                sb.AppendLine("⚠️ 搜索被中断");

            if (result.Files.Count == 0)
            {
                sb.AppendLine("✓ 没有找到匹配的文件");
                return sb.ToString();
            }

            sb.AppendLine($"✓ 找到 {result.Files.Count} 个文件 ({result.ElapsedMs}ms):");
            sb.AppendLine();

            foreach (var file in result.Files)
            {
                sb.AppendLine($"  {file.Name} ({file.SizeFormatted}) - {file.Modified:yyyy-MM-dd HH:mm}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索文件失败: {RootPath} 模式: {Pattern}", rootPath, pattern);
            return $"✗ 错误：{ex.Message}";
        }
    }

    private async Task<string> GetDrivesAsync(CancellationToken ct = default)
    {
        try
        {
            var drives = _fileBrowser.GetAvailableDrives();

            if (drives.Count == 0)
                return "✗ 没有找到可用的驱动器";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("✓ 可用的驱动器:");

            foreach (var drive in drives)
            {
                sb.AppendLine($"  💾 {drive}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取驱动器列表失败");
            return $"✗ 错误：{ex.Message}";
        }
    }

    private async Task<string> OpenInExplorerAsync(
        string path,
        string mode = "open",
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "✗ 错误：路径不能为空";

            var result = _fileBrowser.OpenInExplorer(path, mode);
            return result.StartsWith("错误", StringComparison.Ordinal)
                ? $"✗ {result}"
                : result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "资源管理器操作失败: {Path} Mode: {Mode}", path, mode);
            return $"✗ 错误：{ex.Message}";
        }
    }
}