
namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆写入和透明化查看工具核心处理器接口。
/// 负责人工记忆写入、最近记忆查询等操作。
/// </summary>
public interface IMemoryWriteToolHandler
{

    /// <summary>
    /// 用户明确授权时，写入一条人工记忆。
    /// </summary>
    /// <param name="content">记忆内容，支持自然语言描述。</param>
    /// <param name="memoryType">记忆类型，如"note"、"rule"、"fact"等。</param>
    /// <param name="topic">主题标签，用于归类记忆。</param>
    /// <param name="reason">写入原因或备注，便于后续追溯。</param>
    /// <param name="workspaceId">当前工作区唯一标识。</param>
    /// <param name="userConfirmed">用户是否已明确授权写入，true 表示已确认。</param>
    /// <returns>写入结果，通常为 JSON 字符串。</returns>
    string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed);


    /// <summary>
    /// 查看最近生成或访问的记忆。
    /// </summary>
    /// <param name="limit">返回的最大条数。</param>
    /// <param name="kind">记忆类型筛选，如"note"、"fact"、"all"等。</param>
    /// <param name="workspaceId">当前工作区唯一标识。</param>
    /// <returns>最近记忆列表，通常为 JSON 字符串。</returns>
    string ListRecent(int limit, string kind, string workspaceId);
}
