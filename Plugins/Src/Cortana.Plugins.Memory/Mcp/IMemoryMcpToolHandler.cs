namespace Cortana.Plugins.Memory.Mcp;

/// <summary>
/// MCP 独立模式专用工具处理器。
/// </summary>
public interface IMemoryMcpToolHandler
{
    /// <summary>
    /// 记录一条 MCP 客户端提供的对话消息。
    /// </summary>
    string RecordTurn(string role, string content, string agentId, string workspaceId, string sessionId, string turnId, string messageId, string source, long createdTimestamp);

    /// <summary>
    /// 设置当前 MCP 进程默认记忆作用域。
    /// </summary>
    string SetScope(string agentId, string workspaceId, string source);

    /// <summary>
    /// 获取当前 MCP 进程默认记忆作用域。
    /// </summary>
    string GetScope();
}
