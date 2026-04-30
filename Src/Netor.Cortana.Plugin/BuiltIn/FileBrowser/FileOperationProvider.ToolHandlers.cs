using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

public sealed partial class FileOperationProvider
{
    private Task<string> CreateFileToolAsync(string? path, string? content = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("create_file", string.Empty, "缺少必填参数 path"));

        return CreateFileAsync(path, content ?? string.Empty);
    }

    private Task<string> WriteFileToolAsync(string? path, string? content = null, bool? backup = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("write_file", string.Empty, "缺少必填参数 path"));

        return WriteFileAsync(path, content ?? string.Empty, backup ?? true);
    }

    private Task<string> WriteLargeFileToolAsync(
        string? path,
        string? content = null,
        bool? overwrite = null,
        bool? backup = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("write_large_file", string.Empty, "缺少必填参数 path"));

        return WriteLargeFileAsync(path, content ?? string.Empty, overwrite ?? true, backup ?? true);
    }

    private Task<string> EditFileToolAsync(
        string? path,
        string? operation,
        int? startLine,
        int? endLine = null,
        string? content = null,
        bool? backup = null,
        string? expectedHash = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("edit_file", string.Empty, "缺少必填参数 path"));

        if (string.IsNullOrWhiteSpace(operation))
            return Task.FromResult(BuildErrorJson("edit_file", path, "缺少必填参数 operation"));

        if (startLine is null)
            return Task.FromResult(BuildErrorJson("edit_file", path, "缺少必填参数 startLine"));

        return EditFileAsync(path, operation, startLine.Value, endLine, content, backup ?? true, expectedHash);
    }

    private Task<string> DeleteFileToolAsync(string? path, bool? backup = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("delete_file", string.Empty, "缺少必填参数 path"));

        return DeleteFileAsync(path, backup ?? true);
    }

    private Task<string> MoveFileToolAsync(string? sourcePath, string? destPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return Task.FromResult(BuildErrorJson("move_file", string.Empty, "缺少必填参数 sourcePath"));

        if (string.IsNullOrWhiteSpace(destPath))
            return Task.FromResult(BuildErrorJson("move_file", sourcePath, "缺少必填参数 destPath"));

        return MoveFileAsync(sourcePath, destPath);
    }

    private Task<string> CreateDirectoryToolAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("create_directory", string.Empty, "缺少必填参数 path"));

        return CreateDirectoryAsync(path);
    }

    private Task<string> DeleteDirectoryToolAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(BuildErrorJson("delete_directory", string.Empty, "缺少必填参数 path"));

        return DeleteDirectoryAsync(path);
    }

    private Task<string> WriteFilesBatchAsync(
        List<FileOperator.BatchWriteFile> files,
        bool backup = true,
        bool stopOnError = false)
    {
        try
        {
            if (files.Count == 0)
                return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, "files 不能为空"));

            var parsedFiles = new List<FileOperator.BatchWriteFile>();
            foreach (var item in files)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, "files[*].path 必须是字符串"));
                }

                parsedFiles.Add(new FileOperator.BatchWriteFile
                {
                    Path = item.Path,
                    Content = item.Content ?? string.Empty,
                    Overwrite = item.Overwrite ?? true
                });
            }

            var result = _fileOperator.SysWriteFilesBatch(parsedFiles, backup, stopOnError);
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
}
