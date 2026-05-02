namespace Cortana.Plugins.Memory.Mcp;

/// <summary>
/// MCP 独立运行模式配置。
/// </summary>
public sealed class MemoryMcpRuntimeOptions
{
    /// <summary>
    /// 数据库存放目录。
    /// </summary>
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// 数据库文件名。
    /// </summary>
    public string DatabaseFileName { get; init; } = "memory.db";

    /// <summary>
    /// 默认调用方标识。
    /// </summary>
    public string DefaultAgentId { get; init; } = "mcp-default";

    /// <summary>
    /// 默认工作区标识。
    /// </summary>
    public string DefaultWorkspaceId { get; init; } = "default";

    /// <summary>
    /// 默认来源。
    /// </summary>
    public string DefaultSource { get; init; } = "mcp";

    /// <summary>
    /// 是否启用后台处理。
    /// </summary>
    public bool EnableAutoProcessing { get; init; } = true;
}
