using System.ComponentModel;
using Cortana.Plugins.Memory.ToolHandlers;
using ModelContextProtocol.Server;

namespace Cortana.Plugins.Memory.Mcp;

/// <summary>
/// 记忆插件 MCP 工具适配器。
/// 仅做协议适配，所有业务规则与参数归一化均委托 <see cref="IMemoryReadToolHandler"/> 与 <see cref="IMemoryWriteToolHandler"/>。
/// </summary>
[McpServerToolType]
public sealed class MemoryMcpTools(
    IMemoryReadToolHandler readHandler,
    IMemoryWriteToolHandler writeHandler,
    IMemoryMcpToolHandler mcpHandler)
{
    [McpServerTool(Name = "memory_record_turn")]
    [Description("记录一条 MCP 客户端提供的对话消息，进入 observation 和后续长期记忆处理链路。")]
    public string RecordTurn(
        [Description("消息角色，支持 user、assistant、system、tool")] string role,
        [Description("消息内容")] string content,
        [Description("智能体标识，空字符串表示使用当前默认作用域")] string agentId = "",
        [Description("工作区标识，空字符串表示使用当前默认作用域")] string workspaceId = "",
        [Description("会话标识，空字符串表示自动生成")] string sessionId = "",
        [Description("轮次标识，空字符串表示自动生成")] string turnId = "",
        [Description("消息标识，空字符串表示自动生成")] string messageId = "",
        [Description("来源，空字符串表示使用当前默认来源")] string source = "",
        [Description("Unix 毫秒时间戳，0 表示当前时间")] long createdTimestamp = 0)
        => mcpHandler.RecordTurn(role, content, agentId, workspaceId, sessionId, turnId, messageId, source, createdTimestamp);

    [McpServerTool(Name = "memory_set_scope")]
    [Description("设置当前 MCP 进程默认记忆作用域。空参数保持原值不变。")]
    public string SetScope(
        [Description("默认智能体标识，空字符串表示保持不变")] string agentId = "",
        [Description("默认工作区标识，空字符串表示保持不变")] string workspaceId = "",
        [Description("默认来源，空字符串表示保持不变")] string source = "")
        => mcpHandler.SetScope(agentId, workspaceId, source);

    [McpServerTool(Name = "memory_get_scope")]
    [Description("查看当前 MCP 进程默认记忆作用域。")]
    public string GetScope()
        => mcpHandler.GetScope();

    [McpServerTool(Name = "memory_recall")]
    [Description("根据查询文本召回相关长期记忆。只调用记忆召回服务，不暴露数据库路径、SQL 或内部表结构。")]
    public string Recall(
        [Description("查询文本")] string queryText,
        [Description("查询意图，空字符串表示不指定")] string queryIntent = "",
        [Description("智能体标识，空字符串表示不按智能体过滤")] string agentId = "",
        [Description("工作区标识，空字符串表示不指定")] string workspaceId = "",
        [Description("最大返回记忆数量，0 表示使用系统默认，上限 50")] int maxMemoryCount = 0)
        => readHandler.Recall(queryText, queryIntent, agentId, workspaceId, maxMemoryCount);

    [McpServerTool(Name = "memory_supply_context")]
    [Description("根据当前任务和最近消息生成可供上层注入的结构化记忆包。输出分组、条目、预算和策略，不负责最终 prompt 拼接。")]
    public string SupplyContext(
        [Description("场景，空字符串表示不指定")] string scenario = "",
        [Description("当前任务，空字符串表示不指定")] string currentTask = "",
        [Description("最近消息，每行一条；空字符串表示不提供")] string recentMessages = "",
        [Description("工作区标识，空字符串表示不指定")] string workspaceId = "",
        [Description("最大供应记忆数量，0 表示使用系统默认，上限 50")] int maxMemoryCount = 0,
        [Description("最大 Token 预算，0 表示不限制")] int maxTokenBudget = 0,
        [Description("触发来源，空字符串表示工具调用")] string triggerSource = "")
        => readHandler.SupplyContext(scenario, currentTask, recentMessages, workspaceId, maxMemoryCount, maxTokenBudget, triggerSource);

    [McpServerTool(Name = "memory_get_status")]
    [Description("查看记忆系统基础状态，返回观察记录、记忆片段、抽象记忆、召回日志数量和处理状态，不暴露数据库路径。")]
    public string GetStatus(
        [Description("工作区标识，空字符串表示不指定")] string workspaceId = "")
        => readHandler.GetStatus(workspaceId);

    [McpServerTool(Name = "memory_add_note")]
    [Description("用户明确要求记住、写入记忆或加入长期记忆时，写入一条人工记忆。默认写入候选区并记录审计，不允许静默调用。")]
    public string AddNote(
        [Description("需要写入的记忆内容")] string content,
        [Description("用户授权写入的原因，不能为空")] string reason,
        [Description("是否已获得用户明确授权，必须为 true")] bool userConfirmed,
        [Description("记忆类型，支持 fact、preference、task、constraint、note")] string memoryType = "note",
        [Description("主题，空字符串表示按记忆类型归类")] string topic = "",
        [Description("工作区标识，空字符串表示不指定")] string workspaceId = "")
        => writeHandler.AddNote(content, memoryType, topic, reason, workspaceId, userConfirmed);

    [McpServerTool(Name = "memory_list_recent")]
    [Description("查看最近生成或访问的记忆，用于验收、透明化展示和了解系统最近记住了什么。")]
    public string ListRecent(
        [Description("最多返回数量，0 表示默认，上限 50")] int limit = 0,
        [Description("记忆类别，支持 fragment、abstraction 或空字符串")] string kind = "",
        [Description("工作区标识，空字符串表示不指定")] string workspaceId = "")
        => writeHandler.ListRecent(limit, kind, workspaceId);
}
