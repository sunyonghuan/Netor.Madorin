namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆读取类工具核心处理器。
/// </summary>
public interface IMemoryReadToolHandler
{
    /// <summary>根据查询文本召回相关长期记忆。</summary>
    string Recall(string queryText, string queryIntent, string workspaceId, int maxMemoryCount);

    /// <summary>根据当前任务和最近消息生成结构化记忆供应包。</summary>
    string SupplyContext(string scenario, string currentTask, string recentMessages, string workspaceId, int maxMemoryCount, int maxTokenBudget, string triggerSource);

    /// <summary>查看记忆系统基础状态。</summary>
    string GetStatus(string workspaceId);
}
