using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件操作器 — 在工作目录范围内执行文件的创建、修改、删除、移动等操作。
/// 所有路径操作均强制约束在 <see cref="IAppPaths.WorkspaceDirectory"/> 之内。
/// </summary>
public sealed class FileOperator
{
    private const string WorkspaceBoundaryViolationMessage = "当前路径超出工作目录边界。如需访问工作目录外的路径，请先提醒用户并获得用户明确同意。";

    private readonly IAppPaths _appPaths;
    private readonly ILogger<FileOperator> _logger;

    private const string BackupFolder = ".cortana/backups";

    public FileOperator(IAppPaths appPaths, ILogger<FileOperator> logger)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// 文件工具统一结果模型。
    /// </summary>
    public sealed record FileToolResult
    {
        public string Tool { get; init; } = string.Empty;

        public bool Success { get; init; }

        public string Path { get; init; } = string.Empty;

        public string? TargetPath { get; init; }

        public string? Message { get; init; }

        public string? Error { get; init; }

        public string? BackupPath { get; init; }

        public string? Operation { get; init; }

        public long? BytesWritten { get; init; }

        public int? StartLine { get; init; }

        public int? EndLine { get; init; }

        public int? ChangedLineCount { get; init; }

        public int? SuccessCount { get; init; }

        public int? FailCount { get; init; }

        public List<FileToolResult>? Items { get; init; }
    }

    /// <summary>
    /// 工具：大文件写入（sys_write_large_file）
    /// 参数：
    ///   - path: 文件路径（绝对或相对工作区）
    ///   - content: 文件完整内容，无大小限制
    ///   - overwrite: 是否覆盖，默认 true
    ///   - backup: 是否备份，默认 true
    /// 返回：
    ///   - success: 是否成功
    ///   - path: 实际写入路径
    ///   - bytesWritten: 写入字节数
    /// </summary>
    public WriteLargeFileResult SysWriteLargeFile(
        string path,
        string content,
        bool overwrite = true,
        bool backup = true)
    {
        try
        {
            var fullPath = ResolveSafePath(path);
            var fileExists = File.Exists(fullPath);

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (fileExists && !overwrite)
            {
                _logger.LogWarning("文件已存在且未设置覆盖：{Path}", fullPath);
                return new WriteLargeFileResult(false, fullPath, 0, "文件已存在且未设置覆盖", null);
            }

            string? backupPath = null;
            if (fileExists && backup)
            {
                backupPath = BackupFile(fullPath);
            }

            var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            File.WriteAllBytes(fullPath, bytes);

            _logger.LogInformation("大文件写入：{Path} 字节数：{Bytes}", fullPath, bytes.Length);
            return new WriteLargeFileResult(true, fullPath, bytes.Length, null, backupPath);
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "大文件写入失败：{Path}", path);
            return new WriteLargeFileResult(false, path, 0, ex.Message, null);
        }
    }

    /// <summary>
    /// 工具：批量文件写入（sys_write_files_batch）
    /// 参数：
    ///   - files: 文件列表（path, content, overwrite）
    ///   - backup: 统一备份开关，默认 true
    ///   - stopOnError: 遇错停止，默认 false
    /// 返回：
    ///   - results: 每个文件的写入结果（path, success, error）
    ///   - successCount: 成功数
    ///   - failCount: 失败数
    /// </summary>
    public BatchWriteFilesResult SysWriteFilesBatch(
        List<BatchWriteFile> files,
        bool backup = true,
        bool stopOnError = false)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new List<BatchWriteResult>();
        var successCount = 0;
        var failCount = 0;

        foreach (var file in files)
        {
            try
            {
                var fullPath = ResolveSafePath(file.Path);
                var fileExists = File.Exists(fullPath);

                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (fileExists && !(file.Overwrite ?? true))
                {
                    results.Add(new BatchWriteResult
                    {
                        Path = fullPath,
                        Success = false,
                        Error = "文件已存在且未设置覆盖",
                        BackupPath = null
                    });
                    failCount++;

                    if (stopOnError)
                        break;

                    continue;
                }

                string? backupPath = null;
                if (fileExists && backup)
                {
                    backupPath = BackupFile(fullPath);
                }

                var bytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
                File.WriteAllBytes(fullPath, bytes);

                results.Add(new BatchWriteResult
                {
                    Path = fullPath,
                    Success = true,
                    Error = string.Empty,
                    BackupPath = backupPath
                });
                successCount++;
            }
            catch (Exception ex) when (ex is not SecurityException)
            {
                var errorMessage = ex.Message;
                results.Add(new BatchWriteResult
                {
                    Path = file.Path,
                    Success = false,
                    Error = errorMessage,
                    BackupPath = null
                });
                failCount++;

                _logger.LogError(ex, "批量写入文件失败：{Path}", file.Path);

                if (stopOnError)
                    break;
            }
        }

        return new BatchWriteFilesResult(results, successCount, failCount);
    }

    /// <summary>
    /// 大文件写入结果。
    /// </summary>
    public sealed record WriteLargeFileResult(
        bool Success,
        string Path,
        long BytesWritten,
        string? Error,
        string? BackupPath);

    /// <summary>
    /// 批量文件写入结果。
    /// </summary>
    public sealed record BatchWriteFilesResult(
        List<BatchWriteResult> Results,
        int SuccessCount,
        int FailCount);

    /// <summary>
    /// 批量写入单文件参数。
    /// </summary>
    public sealed class BatchWriteFile
    {
        public string Path { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public bool? Overwrite { get; set; } = true;
    }

    /// <summary>
    /// 批量写入结果。
    /// </summary>
    public sealed class BatchWriteResult
    {
        public string Path { get; set; } = string.Empty;

        public bool Success { get; set; }

        public string Error { get; set; } = string.Empty;

        public string? BackupPath { get; set; }
    }

    // ──────── 文件操作 ────────

    /// <summary>
    /// 创建新文件（不覆盖已存在的文件）。
    /// </summary>
    public FileToolResult CreateFile(string path, string content)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (File.Exists(fullPath))
            {
                return new FileToolResult
                {
                    Tool = "create_file",
                    Success = false,
                    Path = fullPath,
                    Error = $"文件已存在 - {fullPath}",
                    Message = $"错误：文件已存在 - {fullPath}"
                };
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content ?? "", Encoding.UTF8);

            _logger.LogInformation("文件已创建：{Path}", fullPath);
            return new FileToolResult
            {
                Tool = "create_file",
                Success = true,
                Path = fullPath,
                Message = $"文件已创建：{fullPath}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "创建文件失败：{Path}", path);
            return new FileToolResult
            {
                Tool = "create_file",
                Success = false,
                Path = path,
                Error = ex.Message,
                Message = $"错误：创建文件失败 - {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 写入文件内容；文件不存在时创建，已存在时覆盖。
    /// </summary>
    public FileToolResult WriteFile(string path, string content, bool backup = true)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            var fileExists = File.Exists(fullPath);

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string? backupPath = null;
            if (fileExists && backup)
            {
                backupPath = BackupFile(fullPath);
            }

            if (fileExists)
            {
                var document = TextFileDocument.Load(fullPath);
                document.SetContent(content ?? string.Empty);
                document.Save();
            }
            else
            {
                File.WriteAllText(fullPath, content ?? "", new UTF8Encoding(false));
            }

            if (!fileExists)
            {
                _logger.LogInformation("文件已写入：{Path}", fullPath);
                return new FileToolResult
                {
                    Tool = "write_file",
                    Success = true,
                    Path = fullPath,
                    Message = $"文件已写入：{fullPath}"
                };
            }

            _logger.LogInformation("文件已写入：{Path}", fullPath);
            return new FileToolResult
            {
                Tool = "write_file",
                Success = true,
                Path = fullPath,
                BackupPath = backupPath,
                Message = backupPath is not null
                    ? $"文件已写入：{fullPath}，已备份到 {backupPath}"
                    : $"文件已写入：{fullPath}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "写入文件失败：{Path}", path);
            return new FileToolResult
            {
                Tool = "write_file",
                Success = false,
                Path = path,
                Error = ex.Message,
                Message = $"错误：写入文件失败 - {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 按 1-based 行号编辑文件内容。
    /// </summary>
    internal FileEditResult EditFile(
        string path,
        string operation,
        int startLine,
        int? endLine = null,
        string? content = null,
        bool backup = true,
        string? expectedHash = null)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (!File.Exists(fullPath))
                return FileEditResult.CreateError(path, "文件不存在");

            var document = TextFileDocument.Load(fullPath);

            if (!string.IsNullOrWhiteSpace(expectedHash)
                && !string.Equals(document.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return FileEditResult.CreateError(path, "文件内容已变化，请先重新调用 sys_read_file 获取最新行号和哈希");
            }

            var normalizedOperation = operation?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedOperation))
                return FileEditResult.CreateError(path, "operation 不能为空，仅支持 replace / insert / delete");

            string? backupPath = null;
            if (backup)
            {
                backupPath = BackupFile(fullPath);
            }

            return normalizedOperation switch
            {
                "replace" => ReplaceLines(path, document, startLine, endLine, content, backupPath),
                "insert" => InsertLines(path, document, startLine, content, backupPath),
                "delete" => DeleteLines(path, document, startLine, endLine, backupPath),
                _ => FileEditResult.CreateError(path, $"不支持的 operation: {operation}，仅支持 replace / insert / delete")
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "按行编辑文件失败：{Path} Operation: {Operation}", path, operation);
            return FileEditResult.CreateError(path, $"按行编辑文件失败 - {ex.Message}");
        }
    }

    /// <summary>
    /// 删除文件。
    /// </summary>
    public FileToolResult DeleteFile(string path, bool backup = true)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (!File.Exists(fullPath))
            {
                return new FileToolResult
                {
                    Tool = "delete_file",
                    Success = false,
                    Path = fullPath,
                    Error = $"文件不存在 - {fullPath}",
                    Message = $"错误：文件不存在 - {fullPath}"
                };
            }

            string? backupPath = null;
            if (backup)
                backupPath = BackupFile(fullPath);

            File.Delete(fullPath);

            _logger.LogInformation("文件已删除：{Path}", fullPath);
            return new FileToolResult
            {
                Tool = "delete_file",
                Success = true,
                Path = fullPath,
                BackupPath = backupPath,
                Message = backupPath is not null
                    ? $"文件已删除：{fullPath}，已备份到 {backupPath}"
                    : $"文件已删除：{fullPath}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "删除文件失败：{Path}", path);
            return new FileToolResult
            {
                Tool = "delete_file",
                Success = false,
                Path = path,
                Error = ex.Message,
                Message = $"错误：删除文件失败 - {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 移动/重命名文件。
    /// </summary>
    public FileToolResult MoveFile(string sourcePath, string destPath)
    {
        try
        {
            var fullSource = ResolveSafePath(sourcePath);
            var fullDest = ResolveSafePath(destPath);

            if (!File.Exists(fullSource))
            {
                return new FileToolResult
                {
                    Tool = "move_file",
                    Success = false,
                    Path = fullSource,
                    TargetPath = fullDest,
                    Error = $"源文件不存在 - {fullSource}",
                    Message = $"错误：源文件不存在 - {fullSource}"
                };
            }

            if (File.Exists(fullDest))
            {
                return new FileToolResult
                {
                    Tool = "move_file",
                    Success = false,
                    Path = fullSource,
                    TargetPath = fullDest,
                    Error = $"目标文件已存在 - {fullDest}",
                    Message = $"错误：目标文件已存在 - {fullDest}"
                };
            }

            var destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Move(fullSource, fullDest);

            _logger.LogInformation("文件已移动：{Source} -> {Dest}", fullSource, fullDest);
            return new FileToolResult
            {
                Tool = "move_file",
                Success = true,
                Path = fullSource,
                TargetPath = fullDest,
                Message = $"文件已移动：{fullSource} -> {fullDest}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "移动文件失败：{Source} -> {Dest}", sourcePath, destPath);
            return new FileToolResult
            {
                Tool = "move_file",
                Success = false,
                Path = sourcePath,
                TargetPath = destPath,
                Error = ex.Message,
                Message = $"错误：移动文件失败 - {ex.Message}"
            };
        }
    }

    // ──────── 文件夹操作 ────────

    /// <summary>
    /// 创建文件夹（含递归创建父目录）。
    /// </summary>
    public FileToolResult CreateDirectory(string path)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (Directory.Exists(fullPath))
            {
                return new FileToolResult
                {
                    Tool = "create_directory",
                    Success = true,
                    Path = fullPath,
                    Message = $"文件夹已存在：{fullPath}"
                };
            }

            Directory.CreateDirectory(fullPath);

            _logger.LogInformation("文件夹已创建：{Path}", fullPath);
            return new FileToolResult
            {
                Tool = "create_directory",
                Success = true,
                Path = fullPath,
                Message = $"文件夹已创建：{fullPath}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "创建文件夹失败：{Path}", path);
            return new FileToolResult
            {
                Tool = "create_directory",
                Success = false,
                Path = path,
                Error = ex.Message,
                Message = $"错误：创建文件夹失败 - {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 删除空文件夹。
    /// </summary>
    public FileToolResult DeleteDirectory(string path)
    {
        try
        {
            var fullPath = ResolveSafePath(path);

            if (!Directory.Exists(fullPath))
            {
                return new FileToolResult
                {
                    Tool = "delete_directory",
                    Success = false,
                    Path = fullPath,
                    Error = $"文件夹不存在 - {fullPath}",
                    Message = $"错误：文件夹不存在 - {fullPath}"
                };
            }

            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                return new FileToolResult
                {
                    Tool = "delete_directory",
                    Success = false,
                    Path = fullPath,
                    Error = $"文件夹非空，不允许删除 - {fullPath}",
                    Message = $"错误：文件夹非空，不允许删除 - {fullPath}"
                };
            }

            Directory.Delete(fullPath);

            _logger.LogInformation("文件夹已删除：{Path}", fullPath);
            return new FileToolResult
            {
                Tool = "delete_directory",
                Success = true,
                Path = fullPath,
                Message = $"文件夹已删除：{fullPath}"
            };
        }
        catch (Exception ex) when (ex is not SecurityException)
        {
            _logger.LogError(ex, "删除文件夹失败：{Path}", path);
            return new FileToolResult
            {
                Tool = "delete_directory",
                Success = false,
                Path = path,
                Error = ex.Message,
                Message = $"错误：删除文件夹失败 - {ex.Message}"
            };
        }
    }

    // ──────── 安全校验 ────────

    /// <summary>
    /// 解析路径并校验其在工作目录范围内。
    /// </summary>
    private string ResolveSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new SecurityException("路径不能为空");

        var workspace = Path.GetFullPath(_appPaths.WorkspaceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspace, path));

        if (!fullPath.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(workspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"{WorkspaceBoundaryViolationMessage} 路径：{fullPath}");
        }

        return fullPath;
    }

    // ──────── 备份 ────────

    /// <summary>
    /// 备份文件到 .cortana/backups/ 目录下，返回备份路径。
    /// </summary>
    private string BackupFile(string fullPath)
    {
        var workspace = Path.GetFullPath(_appPaths.WorkspaceDirectory);
        var relativePath = Path.GetRelativePath(workspace, fullPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(workspace, BackupFolder, $"{relativePath}.{timestamp}.bak");

        var backupDir = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(backupDir) && !Directory.Exists(backupDir))
            Directory.CreateDirectory(backupDir);

        File.Copy(fullPath, backupPath, overwrite: true);

        _logger.LogInformation("文件已备份：{Source} -> {Backup}", fullPath, backupPath);
        return backupPath;
    }

    /// <summary>
    /// 安全异常，用于路径越界。
    /// </summary>
    private sealed class SecurityException(string message) : Exception(message);

    private FileEditResult ReplaceLines(
        string path,
        TextFileDocument document,
        int startLine,
        int? endLine,
        string? content,
        string? backupPath)
    {
        if (endLine is null)
            return FileEditResult.CreateError(path, "replace 操作必须提供 endLine");

        if (content is null)
            return FileEditResult.CreateError(path, "replace 操作必须提供 content");

        if (!TryValidateLineRange(document.TotalLines, startLine, endLine.Value, out var rangeError))
            return FileEditResult.CreateError(path, rangeError);

        var replacementLines = TextFileDocument.ParseContentLines(content);
        document.Lines.RemoveRange(startLine - 1, endLine.Value - startLine + 1);
        document.Lines.InsertRange(startLine - 1, replacementLines);
        document.Save();

        var changedLineCount = Math.Max(endLine.Value - startLine + 1, replacementLines.Count);
        var affectedEndLine = replacementLines.Count == 0 ? startLine - 1 : startLine + replacementLines.Count - 1;

        _logger.LogInformation("文件已按行替换：{Path} {StartLine}-{EndLine}", document.FullPath, startLine, endLine.Value);

        return FileEditResult.CreateSuccess(
            path: document.FullPath,
            operation: "replace",
            startLine: startLine,
            endLine: affectedEndLine,
            changedLineCount: changedLineCount,
            backupPath: backupPath);
    }

    private FileEditResult InsertLines(
        string path,
        TextFileDocument document,
        int startLine,
        string? content,
        string? backupPath)
    {
        if (content is null)
            return FileEditResult.CreateError(path, "insert 操作必须提供 content");

        if (startLine < 1 || startLine > document.TotalLines + 1)
        {
            return FileEditResult.CreateError(
                path,
                $"startLine 越界：{startLine}，有效范围为 1 到 {document.TotalLines + 1}");
        }

        var insertedLines = TextFileDocument.ParseContentLines(content);
        document.Lines.InsertRange(startLine - 1, insertedLines);
        document.Save();

        var affectedEndLine = insertedLines.Count == 0 ? startLine - 1 : startLine + insertedLines.Count - 1;

        _logger.LogInformation("文件已按行插入：{Path} BeforeLine: {StartLine}", document.FullPath, startLine);

        return FileEditResult.CreateSuccess(
            path: document.FullPath,
            operation: "insert",
            startLine: startLine,
            endLine: affectedEndLine,
            changedLineCount: insertedLines.Count,
            backupPath: backupPath);
    }

    private FileEditResult DeleteLines(
        string path,
        TextFileDocument document,
        int startLine,
        int? endLine,
        string? backupPath)
    {
        if (endLine is null)
            return FileEditResult.CreateError(path, "delete 操作必须提供 endLine");

        if (!TryValidateLineRange(document.TotalLines, startLine, endLine.Value, out var rangeError))
            return FileEditResult.CreateError(path, rangeError);

        document.Lines.RemoveRange(startLine - 1, endLine.Value - startLine + 1);
        document.Save();

        _logger.LogInformation("文件已按行删除：{Path} {StartLine}-{EndLine}", document.FullPath, startLine, endLine.Value);

        return FileEditResult.CreateSuccess(
            path: document.FullPath,
            operation: "delete",
            startLine: startLine,
            endLine: endLine.Value,
            changedLineCount: endLine.Value - startLine + 1,
            backupPath: backupPath);
    }

    private static bool TryValidateLineRange(int totalLines, int startLine, int endLine, out string error)
    {
        if (totalLines == 0)
        {
            error = "文件为空，没有可编辑的行";
            return false;
        }

        if (startLine < 1 || startLine > totalLines)
        {
            error = $"startLine 越界：{startLine}，有效范围为 1 到 {totalLines}";
            return false;
        }

        if (endLine < startLine || endLine > totalLines)
        {
            error = $"endLine 越界：{endLine}，有效范围为 {startLine} 到 {totalLines}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal sealed class TextFileDocument
{
    private static readonly UTF8Encoding StrictUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private TextFileDocument(
        string fullPath,
        List<string> lines,
        Encoding encoding,
        bool hasBom,
        string newLine,
        bool endsWithNewLine,
        string hash)
    {
        FullPath = fullPath;
        Lines = lines;
        Encoding = encoding;
        HasBom = hasBom;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
        Hash = hash;
    }

    public string FullPath { get; }

    public List<string> Lines { get; }

    public Encoding Encoding { get; }

    public bool HasBom { get; }

    public string NewLine { get; }

    public bool EndsWithNewLine { get; private set; }

    public string Hash { get; }

    public int TotalLines => Lines.Count;

    public string EncodingDisplayName => HasBom ? $"{Encoding.WebName} (BOM)" : Encoding.WebName;

    public string NewLineDisplayName => NewLine == "\r\n" ? "CRLF" : "LF";

    public static TextFileDocument Load(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var (encoding, preambleLength, hasBom) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var endsWithNewLine = content.EndsWith("\r\n", StringComparison.Ordinal)
            || content.EndsWith("\n", StringComparison.Ordinal);
        var newLine = DetectNewLine(content);
        var lines = ParseContentLines(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        return new TextFileDocument(fullPath, lines, encoding, hasBom, newLine, endsWithNewLine, hash);
    }

    public void SetContent(string content)
    {
        var normalizedContent = content ?? string.Empty;
        EndsWithNewLine = normalizedContent.EndsWith("\r\n", StringComparison.Ordinal)
            || normalizedContent.EndsWith("\n", StringComparison.Ordinal);

        Lines.Clear();
        Lines.AddRange(ParseContentLines(normalizedContent));
    }

    public void Save()
    {
        var content = Lines.Count == 0
            ? string.Empty
            : string.Join(NewLine, Lines);

        if (EndsWithNewLine && Lines.Count > 0)
        {
            content += NewLine;
        }

        File.WriteAllText(FullPath, content, Encoding);
    }

    public static List<string> ParseContentLines(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var endsWithNewLine = normalized.EndsWith('\n');

        if (endsWithNewLine)
            normalized = normalized[..^1];

        if (normalized.Length == 0)
            return [string.Empty];

        return normalized.Split('\n').ToList();
    }

    private static string DetectNewLine(string content)
    {
        if (content.Contains("\r\n", StringComparison.Ordinal))
            return "\r\n";

        if (content.Contains('\n'))
            return "\n";

        return Environment.NewLine == "\r\n" ? "\r\n" : "\n";
    }

    private static (Encoding Encoding, int PreambleLength, bool HasBom) DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3, true);

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4, true);

        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4, true);

        if (StartsWith(bytes, 0xFF, 0xFE))
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2, true);

        if (StartsWith(bytes, 0xFE, 0xFF))
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2, true);

        try
        {
            StrictUtf8NoBom.GetString(bytes);
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0, false);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Default, 0, false);
        }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;

        for (var index = 0; index < prefix.Length; index++)
        {
            if (bytes[index] != prefix[index])
                return false;
        }

        return true;
    }
}

internal sealed class FileEditResult
{
    private FileEditResult(
        bool isSuccess,
        string path,
        string operation,
        int startLine,
        int endLine,
        int changedLineCount,
        string? backupPath,
        string? errorMessage)
    {
        IsSuccess = isSuccess;
        Path = path;
        Operation = operation;
        StartLine = startLine;
        EndLine = endLine;
        ChangedLineCount = changedLineCount;
        BackupPath = backupPath;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public string Path { get; }

    public string Operation { get; }

    public int StartLine { get; }

    public int EndLine { get; }

    public int ChangedLineCount { get; }

    public string? BackupPath { get; }

    public string? ErrorMessage { get; }

    public static FileEditResult CreateSuccess(
        string path,
        string operation,
        int startLine,
        int endLine,
        int changedLineCount,
        string? backupPath)
        => new(true, path, operation, startLine, endLine, changedLineCount, backupPath, null);

    public static FileEditResult CreateError(string path, string errorMessage)
        => new(false, path, string.Empty, 0, 0, 0, null, errorMessage);
}
