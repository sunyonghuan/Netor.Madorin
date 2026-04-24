using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件操作 AI 工具提供者，提供文件创建、写入、删除、移动等能力。
/// 所有操作严格限制在当前工作目录范围内。
/// </summary>
public sealed partial class FileOperationProvider : AIContextProvider
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
}
