
namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆读取类工具核心处理器接口。
/// 负责根据查询、上下文等参数召回长期记忆、生成记忆包、查询系统状态等。
/// </summary>
public interface IMemoryReadToolHandler
{

    /// <summary>
    /// 根据查询文本召回相关长期记忆。
    /// </summary>
    /// <param name="queryText">用户输入的查询文本，支持自然语言检索。</param>
    /// <param name="queryIntent">查询意图标识，如"recall"、"search"等，用于优化召回策略。</param>
    /// <param name="agentId">可选智能体标识；空字符串表示不按智能体过滤。</param>
    /// <param name="workspaceId">当前工作区唯一标识，用于限定召回范围。</param>
    /// <param name="maxMemoryCount">最大召回记忆条数，防止结果过多。</param>
    /// <returns>召回的长期记忆结果，通常为 JSON 字符串。</returns>
    string Recall(string queryText, string queryIntent, string agentId, string workspaceId, int maxMemoryCount);


    /// <summary>
    /// 根据当前任务和最近消息生成结构化记忆供应包。
    /// </summary>
    /// <param name="scenario">当前场景标识，如"chat"、"tool_call"等。</param>
    /// <param name="currentTask">当前任务描述，便于记忆筛选与上下文注入。</param>
    /// <param name="recentMessages">最近消息内容，通常为 JSON 或文本序列。</param>
    /// <param name="workspaceId">当前工作区唯一标识。</param>
    /// <param name="maxMemoryCount">最大供应记忆条数。</param>
    /// <param name="maxTokenBudget">Token 预算上限，控制召回内容体量。</param>
    /// <param name="triggerSource">触发来源，如"user"、"system"、"tool"等。</param>
    /// <returns>结构化记忆供应包，通常为 JSON 字符串。</returns>
    string SupplyContext(string scenario, string currentTask, string recentMessages, string workspaceId, int maxMemoryCount, int maxTokenBudget, string triggerSource);


    /// <summary>
    /// 查看记忆系统基础状态。
    /// </summary>
    /// <param name="workspaceId">当前工作区唯一标识。</param>
    /// <returns>系统状态信息，通常为 JSON 字符串。</returns>
    string GetStatus(string workspaceId);
}
