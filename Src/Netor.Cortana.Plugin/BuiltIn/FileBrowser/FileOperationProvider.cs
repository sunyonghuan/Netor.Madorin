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
            method: (string path, string content) => _fileOperator.CreateFile(path, content)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_write_file",
            description: "Write or replace a file in the current workspace directory. backup defaults to true.",
            method: (string path, string content, bool backup) => _fileOperator.WriteFile(path, content, backup)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_file",
            description: "Delete a file in the current workspace directory. backup defaults to true.",
            method: (string path, bool backup) => _fileOperator.DeleteFile(path, backup)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_move_file",
            description: "Move or rename a file within the current workspace directory.",
            method: (string sourcePath, string destPath) => _fileOperator.MoveFile(sourcePath, destPath)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_create_directory",
            description: "Create a directory in the current workspace directory, including parent directories when needed.",
            method: (string path) => _fileOperator.CreateDirectory(path)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_delete_directory",
            description: "Delete an empty directory in the current workspace directory. Non-empty directories are not allowed.",
            method: (string path) => _fileOperator.DeleteDirectory(path)));
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
        - sys_write_file and sys_delete_file back up files by default.
        - Backups are stored under .cortana/backups inside the workspace.
        - Keep backup enabled unless the user explicitly asks to disable it.
        - sys_create_file does not overwrite existing files. Use sys_write_file when replacement is intended.
        - sys_delete_directory only works for empty directories.
        - For move operations, both source and destination must stay inside the workspace.
        """;
}
