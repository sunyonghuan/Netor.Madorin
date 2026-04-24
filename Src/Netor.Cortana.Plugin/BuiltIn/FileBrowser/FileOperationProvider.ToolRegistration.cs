using Microsoft.Extensions.AI;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

public sealed partial class FileOperationProvider
{
    /// <summary>
    /// 注册文件与目录操作工具。
    /// </summary>
    private void RegisterTools()
    {
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_create_file",
            description: "Create a new file in the current workspace directory. Fails if the file already exists.",
            method: CreateFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_file",
            description: "Write a file in the current workspace directory. Creates the file if it does not exist, or replaces it if it already exists. backup defaults to true for existing files.",
            method: WriteFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_large_file",
            description: "Write a large file in the current workspace directory. Supports overwrite and optional backup.",
            method: WriteLargeFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_files_batch",
            description: "Write multiple files in a single call. Each file can control overwrite individually. Supports optional backup and stop-on-error behavior.",
            method: WriteFilesBatchAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_edit_file",
            description: "Edit an existing text file by 1-based line numbers. operation supports replace, insert, and delete. Use sys_read_file first to get exact line numbers and hash. backup defaults to true.",
            method: EditFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_file",
            description: "Delete a file in the current workspace directory. backup defaults to true.",
            method: DeleteFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_move_file",
            description: "Move or rename a file within the current workspace directory.",
            method: MoveFileToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_create_directory",
            description: "Create a directory in the current workspace directory, including parent directories when needed.",
            method: CreateDirectoryToolAsync));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_directory",
            description: "Delete an empty directory in the current workspace directory. Non-empty directories are not allowed.",
            method: DeleteDirectoryToolAsync));
    }

    /// <summary>
    /// 构建面向 AI 的文件操作规则说明。
    /// </summary>
    /// <returns>文件操作工具使用说明。</returns>
    private static string BuildInstructions() => """
        ### File Operation Rules

        - Scope: use these tools only inside the current workspace directory.
        - Never create, write, delete, move, or rename paths outside the workspace directory.
        - Paths can be relative or absolute, but absolute paths must be valid and still inside the workspace directory.
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
}
