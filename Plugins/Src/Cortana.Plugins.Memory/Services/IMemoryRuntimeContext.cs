namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 解析当前记忆运行作用域。
/// </summary>
public interface IMemoryRuntimeContext
{
    /// <summary>
    /// 获取当前默认作用域。
    /// </summary>
    MemoryRuntimeScope GetScope();

    /// <summary>
    /// 更新当前默认作用域；空值表示保留原值。
    /// </summary>
    void SetScope(string? agentId, string? workspaceId, string? source);

    /// <summary>
    /// 解析智能体标识，参数为空时回退到默认作用域。
    /// </summary>
    string ResolveAgentId(string? agentId);

    /// <summary>
    /// 解析工作区标识，参数为空时回退到默认作用域。
    /// </summary>
    string? ResolveWorkspaceId(string? workspaceId);

    /// <summary>
    /// 解析来源标识，参数为空时回退到默认作用域。
    /// </summary>
    string ResolveSource(string? source);
}
