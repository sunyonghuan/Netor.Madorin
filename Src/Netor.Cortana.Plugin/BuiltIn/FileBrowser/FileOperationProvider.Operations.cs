using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

public sealed partial class FileOperationProvider
{
    /// <summary>
    /// 按指定行范围编辑文件内容。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="operation">编辑操作类型。</param>
    /// <param name="startLine">起始行号。</param>
    /// <param name="endLine">结束行号。</param>
    /// <param name="content">写入或替换的内容。</param>
    /// <param name="backup">是否创建备份。</param>
    /// <param name="expectedHash">期望的文件哈希值，用于并发校验。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 写入大文件内容。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    /// <param name="overwrite">是否允许覆盖已有文件。</param>
    /// <param name="backup">是否在写入前创建备份。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 创建文件并写入内容。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="content">文件内容。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 写入文件内容。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    /// <param name="backup">是否在写入前创建备份。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 删除指定文件。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="backup">是否在删除前创建备份。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 移动文件到目标位置。
    /// </summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="destPath">目标文件路径。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 创建目录。
    /// </summary>
    /// <param name="path">目录路径。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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

    /// <summary>
    /// 删除目录。
    /// </summary>
    /// <param name="path">目录路径。</param>
    /// <returns>表示操作结果的 JSON 字符串任务。</returns>
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
}
