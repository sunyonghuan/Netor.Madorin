using Cortana.Plugins.Memory.ToolHandlers;
using Netor.Cortana.Plugin;

namespace Cortana.Plugins.Memory.Tools;

/// <summary>
/// 记忆插件 P1 写入和透明化查看工具。
/// </summary>
[Tool]
public sealed class MemoryWriteTools(IMemoryWriteToolHandler handler)
{
    /// <summary>用户明确授权时，写入一条人工记忆。</summary>
    [Tool(Name = "memory_add_note",
        Description = "用户明确要求记住、写入记忆或加入长期记忆时，写入一条人工记忆。默认写入候选区并记录审计，不允许静默调用。")]
    public string AddNote(
        [Parameter(Description = "需要写入的记忆内容")] string content,
        [Parameter(Description = "记忆类型，支持 fact、preference、task、constraint、note")] string memoryType,
        [Parameter(Description = "主题，空字符串表示按记忆类型归类")] string topic,
        [Parameter(Description = "用户授权写入的原因，不能为空")] string reason,
        [Parameter(Description = "工作区标识，空字符串表示不指定")] string workspaceId,
        [Parameter(Description = "是否已获得用户明确授权，必须为 true")] bool userConfirmed)
    {
        return handler.AddNote(content, memoryType, topic, reason, workspaceId, userConfirmed);
    }

    /// <summary>查看最近生成或访问的记忆。</summary>
    [Tool(Name = "memory_list_recent",
        Description = "查看最近生成或访问的记忆，用于验收、透明化展示和了解系统最近记住了什么。")]
    public string ListRecent(
        [Parameter(Description = "最多返回数量，0 表示默认，上限 50")] int limit,
        [Parameter(Description = "记忆类别，支持 fragment、abstraction 或空字符串")] string kind,
        [Parameter(Description = "工作区标识，空字符串表示不指定")] string workspaceId)
    {
        return handler.ListRecent(limit, kind, workspaceId);
    }
}
