using Cortana.Plugins.Memory.ToolHandlers;
using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory.Tools;

/// <summary>
/// 记忆插件 P0 读取类工具。
/// </summary>
[Tool]
public sealed class MemoryReadTools(IMemoryReadToolHandler handler)
{
    /// <summary>根据查询文本召回相关长期记忆。</summary>
    [Tool(Name = "memory_recall",
        Description = "根据查询文本召回相关长期记忆。只调用记忆召回服务，不暴露数据库路径、SQL 或内部表结构。")]
    public string Recall(
        [Parameter(Description = "查询文本")] string queryText,
        [Parameter(Description = "查询意图，空字符串表示不指定")] string queryIntent,
        [Parameter(Description = "工作区标识，空字符串表示不指定")] string workspaceId,
        [Parameter(Description = "最大返回记忆数量，0 表示使用系统默认，上限 50")] int maxMemoryCount)
    {
        return handler.Recall(queryText, queryIntent, workspaceId, maxMemoryCount);
    }

    /// <summary>根据当前任务和最近消息生成结构化记忆供应包。</summary>
    [Tool(Name = "memory_supply_context",
        Description = "根据当前任务和最近消息生成可供上层注入的结构化记忆包。输出分组、条目、预算和策略，不负责最终 prompt 拼接。")]
    public string SupplyContext(
        [Parameter(Description = "场景，空字符串表示不指定")] string scenario,
        [Parameter(Description = "当前任务，空字符串表示不指定")] string currentTask,
        [Parameter(Description = "最近消息，每行一条；空字符串表示不提供")] string recentMessages,
        [Parameter(Description = "工作区标识，空字符串表示不指定")] string workspaceId,
        [Parameter(Description = "最大供应记忆数量，0 表示使用系统默认，上限 50")] int maxMemoryCount,
        [Parameter(Description = "最大 Token 预算，0 表示不限制")] int maxTokenBudget,
        [Parameter(Description = "触发来源，空字符串表示工具调用")] string triggerSource)
    {
        return handler.SupplyContext(scenario, currentTask, recentMessages, workspaceId, maxMemoryCount, maxTokenBudget, triggerSource);
    }

    /// <summary>查看记忆系统基础状态。</summary>
    [Tool(Name = "memory_get_status",
        Description = "查看记忆系统基础状态，返回观察记录、记忆片段、抽象记忆、召回日志数量和处理状态，不暴露数据库路径。")]
    public string GetStatus(
        [Parameter(Description = "工作区标识，空字符串表示不指定")] string workspaceId)
    {
        return handler.GetStatus(workspaceId);
    }
}
