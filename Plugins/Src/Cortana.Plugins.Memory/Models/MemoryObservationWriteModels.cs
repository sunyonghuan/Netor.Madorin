namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// MCP 客户端显式记录单条对话消息的请求。
/// </summary>
public sealed class MemoryRecordTurnRequest
{
    /// <summary>消息角色，例如 user、assistant、system 或 tool。</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>消息正文。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>可选智能体标识；为空时使用运行时默认作用域。</summary>
    public string? AgentId { get; init; }

    /// <summary>可选工作区标识；为空时使用运行时默认作用域。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>可选会话标识；为空时由写入服务生成。</summary>
    public string? SessionId { get; init; }

    /// <summary>可选轮次标识；为空时由写入服务生成。</summary>
    public string? TurnId { get; init; }

    /// <summary>可选消息标识；为空时由写入服务生成。</summary>
    public string? MessageId { get; init; }

    /// <summary>可选来源标识；为空时使用运行时默认来源。</summary>
    public string? Source { get; init; }

    /// <summary>可选 Unix 毫秒时间戳；为空时使用当前时间。</summary>
    public long? CreatedTimestamp { get; init; }
}

/// <summary>
/// MCP 单轮对话消息写入结果。
/// </summary>
public sealed class MemoryRecordTurnResult
{
    /// <summary>写入的 observation 标识。</summary>
    public string ObservationId { get; init; } = string.Empty;

    /// <summary>最终使用的智能体标识。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>最终使用的工作区标识。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>最终使用的会话标识。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>消息角色。</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>写入时使用的 Unix 毫秒时间戳。</summary>
    public long CreatedTimestamp { get; init; }
}

/// <summary>
/// MCP 当前默认记忆作用域。
/// </summary>
public sealed class MemoryScopeResult
{
    /// <summary>默认智能体标识。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>默认工作区标识。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>默认来源标识。</summary>
    public string Source { get; init; } = string.Empty;
}
