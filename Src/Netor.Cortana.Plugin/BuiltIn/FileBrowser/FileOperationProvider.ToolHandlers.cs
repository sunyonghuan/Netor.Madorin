using System.Text.Json;
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
        JsonElement files,
        bool backup = true,
        bool stopOnError = false)
    {
        try
        {
            if (files.ValueKind is not JsonValueKind.Array)
                return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, "files 必须是数组"));

            var parsedFiles = new List<FileOperator.BatchWriteFile>();
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object)
                    return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, "files 中每一项都必须是对象"));

                if (!item.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind is not JsonValueKind.String)
                {
                    return Task.FromResult(BuildErrorJson("write_files_batch", string.Empty, "files[*].path 必须是字符串"));
                }

                string? itemContent = string.Empty;
                if (item.TryGetProperty("content", out var contentElement))
                {
                    if (contentElement.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                    {
                        return Task.FromResult(BuildErrorJson(
                            "write_files_batch",
                            pathElement.GetString() ?? string.Empty,
                            "files[*].content 必须是字符串或 null"));
                    }

                    itemContent = contentElement.ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : contentElement.GetString();
                }

                bool? overwrite = true;
                if (item.TryGetProperty("overwrite", out var overwriteElement))
                {
                    if (overwriteElement.ValueKind is JsonValueKind.True)
                        overwrite = true;
                    else if (overwriteElement.ValueKind is JsonValueKind.False)
                        overwrite = false;
                    else if (overwriteElement.ValueKind is JsonValueKind.Null)
                        overwrite = true;
                    else
                    {
                        return Task.FromResult(BuildErrorJson(
                            "write_files_batch",
                            pathElement.GetString() ?? string.Empty,
                            "files[*].overwrite 必须是布尔值或 null"));
                    }
                }

                parsedFiles.Add(new FileOperator.BatchWriteFile
                {
                    Path = pathElement.GetString() ?? string.Empty,
                    Content = itemContent ?? string.Empty,
                    Overwrite = overwrite
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
