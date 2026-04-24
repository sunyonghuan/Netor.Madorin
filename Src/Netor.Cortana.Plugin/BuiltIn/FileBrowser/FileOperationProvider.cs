using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件操作 AI 工具提供者，提供文件创建、写入、删除、移动等能力。
/// 所有操作严格限制在当前工作目录范围内。
/// </summary>
public sealed class FileOperationProvider : AIContextProvider
{
    private readonly ILogger<FileOperationProvider> _logger;
    private readonly FileOperator _fileOperator;
    private readonly List<AITool> _tools = [];

    /// <summary>
    /// 初始化文件操作工具提供者。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="fileOperator">文件操作服务。</param>
    public FileOperationProvider(ILogger<FileOperationProvider> logger, FileOperator fileOperator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileOperator);

        _logger = logger;
        _fileOperator = fileOperator;
    }

    /// <summary>
    /// 提供当前会话可用的文件操作工具上下文。
    /// </summary>
    /// <param name="context">调用上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含工具集合与使用说明的 AI 上下文。</returns>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
            RegisterTools();

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = BuildInstructions(),
            Tools = _tools
        });
    }

    // ──────── 工具注册 ────────

    /// <summary>
    /// 注册文件与目录操作工具。
    /// </summary>
    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_create_file",
            description: "Create a new file in the current workspace directory. Fails if the file already exists.",
            method: (string path, string content) => CreateFileAsync(path, content)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_file",
            description: "Write a file in the current workspace directory. Creates the file if it does not exist, or replaces it if it already exists. backup defaults to true for existing files.",
            method: (string path, string content, bool backup) => WriteFileAsync(path, content, backup)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_large_file",
            description: "Write a large file in the current workspace directory. Supports overwrite and optional backup.",
            method: WriteLargeFileAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_files_batch",
            description: "Write multiple files in a single call. Each file can control overwrite individually. Supports optional backup and stop-on-error behavior.",
            method: WriteFilesBatchAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_edit_file",
            description: "Edit an existing text file by 1-based line numbers. operation supports replace, insert, and delete. Use sys_read_file first to get exact line numbers and hash. backup defaults to true.",
            method: EditFileAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_file",
            description: "Delete a file in the current workspace directory. backup defaults to true.",
            method: (string path, bool backup) => DeleteFileAsync(path, backup)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_move_file",
            description: "Move or rename a file within the current workspace directory.",
            method: (string sourcePath, string destPath) => MoveFileAsync(sourcePath, destPath)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_create_directory",
            description: "Create a directory in the current workspace directory, including parent directories when needed.",
            method: (string path) => CreateDirectoryAsync(path)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_directory",
            description: "Delete an empty directory in the current workspace directory. Non-empty directories are not allowed.",
            method: (string path) => DeleteDirectoryAsync(path)));
    }

    // ──────── 使用说明 ────────

    /// <summary>
    /// 构建面向 AI 的文件操作规则说明。
    /// </summary>
    /// <returns>文件操作工具使用说明。</returns>
    private static string BuildInstructions() => """
        ### File Operation Rules

        - Scope: use these tools only inside the current workspace directory.
        - Never create, write, delete, move, or rename paths outside the workspace directory.
        - If the user wants to operate on a path outside the workspace, do not use these tools yet. First get explicit user consent to change the workspace directory, then change the workspace directory, then continue.
        - Prefer relative paths when working inside the workspace.
        - Do not use .. to escape the workspace boundary.
        - Tool results are returned as JSON objects with fields like tool, success, path, error, message, and backupPath.
        - sys_write_file and sys_delete_file back up existing files by default.
        - sys_write_large_file writes the full file content in one shot and can optionally overwrite an existing file.
        - sys_write_files_batch writes multiple files in one call and can stop early when one item fails.
        - sys_edit_file backs up the original file by default before applying line-based edits.
        - Backups are stored under .cortana/backups inside the workspace.
        - Keep backup enabled unless the user explicitly asks to disable it.
        - sys_create_file does not overwrite existing files. Use it when creation must fail if the file already exists.
        - sys_write_file creates a new file when missing, or replaces the existing file when present.
        - Before sys_edit_file, call sys_read_file to get exact 1-based line numbers and the latest hash.
        - sys_edit_file operations: replace and delete require startLine/endLine; insert inserts before startLine, and startLine can be totalLines + 1 to append.
        - Pass expectedHash from sys_read_file whenever possible to avoid editing stale content.
        - sys_delete_directory only works for empty directories.
        - For move operations, both source and destination must stay inside the workspace.
        """;

    private Task<string> EditFileAsync(
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
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(BuildErrorJson("edit_file", path, "路径不能为空"));

            if (string.IsNullOrWhiteSpace(operation))
                return Task.FromResult(BuildErrorJson("edit_file", path, "operation 不能为空"));

            var result = _fileOperator.EditFile(path, operation, startLine, endLine, content, backup, expectedHash);
            if (!result.IsSuccess)
                return Task.FromResult(BuildErrorJson("edit_file", result.Path, result.ErrorMessage ?? string.Empty));

            return Task.FromResult(BuildResponseJson(new FileOperator.FileToolResult
            {
                Tool = "edit_file",
                Success = true,
                Path = result.Path,
                Operation = result.Operation,
                StartLine = result.StartLine,
                EndLine = result.EndLine,
                ChangedLineCount = result.ChangedLineCount,
                BackupPath = result.BackupPath
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "按行编辑文件失败: {Path} Operation: {Operation}", path, operation);
            return Task.FromResult(BuildErrorJson("edit_file", path, ex.Message));
        }
    }

    private Task<string> WriteLargeFileAsync(
        string path,
        string content,
        bool overwrite = true,
        bool backup = true)
    {
        try
        {
            var result = _fileOperator.SysWriteLargeFile(path, content, overwrite, backup);
            return Task.FromResult(BuildResponseJson(new FileOperator.FileToolResult
            {
                Tool = "write_large_file",
                Success = result.Success,
                Path = result.Path,
                BytesWritten = result.BytesWritten,
                Error = result.Error,
                BackupPath = result.BackupPath,
                Message = result.Success ? "文件已写入" : string.Empty
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "大文件写入失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("write_large_file", path, ex.Message));
        }
    }

    private Task<string> WriteFilesBatchAsync(
        List<FileOperator.BatchWriteFile> files,
        bool backup = true,
        bool stopOnError = false)
    {
        try
        {
            var result = _fileOperator.SysWriteFilesBatch(files, backup, stopOnError);
            return Task.FromResult(BuildResponseJson(new FileOperator.FileToolResult
            {
                Tool = "write_files_batch",
                Success = result.FailCount == 0,
                SuccessCount = result.SuccessCount,
                FailCount = result.FailCount,
                Items = result.Results.Select(item => new FileOperator.FileToolResult
                {
                    Tool = "write_files_batch_item",
                    Success = item.Success,
                    Path = item.Path,
                    Error = string.IsNullOrWhiteSpace(item.Error) ? null : item.Error,
                    BackupPath = item.BackupPath
                }).ToList(),
                Message = "批量写入完成"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量文件写入失败");
            return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, ex.Message));
        }
    }

    private Task<string> CreateFileAsync(string path, string content)
    {
        try
        {
            var result = _fileOperator.CreateFile(path, content);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建文件失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("create_file", path, ex.Message));
        }
    }

    private Task<string> WriteFileAsync(string path, string content, bool backup)
    {
        try
        {
            var result = _fileOperator.WriteFile(path, content, backup);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入文件失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("write_file", path, ex.Message));
        }
    }

    private Task<string> DeleteFileAsync(string path, bool backup)
    {
        try
        {
            var result = _fileOperator.DeleteFile(path, backup);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除文件失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("delete_file", path, ex.Message));
        }
    }

    private Task<string> MoveFileAsync(string sourcePath, string destPath)
    {
        try
        {
            var result = _fileOperator.MoveFile(sourcePath, destPath);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移动文件失败: {Source} -> {Dest}", sourcePath, destPath);
            return Task.FromResult(BuildErrorJson("move_file", sourcePath, ex.Message, destPath));
        }
    }

    private Task<string> CreateDirectoryAsync(string path)
    {
        try
        {
            var result = _fileOperator.CreateDirectory(path);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建文件夹失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("create_directory", path, ex.Message));
        }
    }

    private Task<string> DeleteDirectoryAsync(string path)
    {
        try
        {
            var result = _fileOperator.DeleteDirectory(path);
            return Task.FromResult(BuildResponseJson(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除文件夹失败: {Path}", path);
            return Task.FromResult(BuildErrorJson("delete_directory", path, ex.Message));
        }
    }

    private Task<string> CreateFileAsync(string path, string content, CancellationToken _)
        => CreateFileAsync(path, content);

    private Task<string> WriteFileAsync(string path, string content, bool backup, CancellationToken _)
        => WriteFileAsync(path, content, backup);

    private Task<string> DeleteFileAsync(string path, bool backup, CancellationToken _)
        => DeleteFileAsync(path, backup);

    private Task<string> MoveFileAsync(string sourcePath, string destPath, CancellationToken _)
        => MoveFileAsync(sourcePath, destPath);

    private Task<string> CreateDirectoryAsync(string path, CancellationToken _)
        => CreateDirectoryAsync(path);

    private Task<string> DeleteDirectoryAsync(string path, CancellationToken _)
        => DeleteDirectoryAsync(path);

    private static string BuildErrorJson(string tool, string path, string errorMessage, string? targetPath = null)
        => BuildResponseJson(new FileOperator.FileToolResult
        {
            Tool = tool,
            Success = false,
            Path = path,
            TargetPath = targetPath,
            Error = errorMessage,
            Message = $"错误：{errorMessage}"
        });

    private static string BuildResponseJson(FileOperator.FileToolResult response)
    {
        var itemsJson = response.Items is null
            ? "null"
            : $"[{string.Join(",", response.Items.Select(BuildItemJson))}]";

        return $"{{\"tool\":{Json(response.Tool)},\"success\":{Bool(response.Success)},\"path\":{Json(response.Path)},\"targetPath\":{Json(response.TargetPath)},\"message\":{Json(response.Message)},\"error\":{Json(response.Error)},\"backupPath\":{Json(response.BackupPath)},\"operation\":{Json(response.Operation)},\"bytesWritten\":{response.BytesWritten?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"startLine\":{response.StartLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"endLine\":{response.EndLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"changedLineCount\":{response.ChangedLineCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"successCount\":{response.SuccessCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"failCount\":{response.FailCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"items\":{itemsJson}}}";
    }

    private static string BuildItemJson(FileOperator.FileToolResult response)
        => BuildResponseJson(response);

    private static string Json(string? value)
        => value is null ? "null" : $"\"{EscapeJson(value)}\"";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string EscapeJson(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
