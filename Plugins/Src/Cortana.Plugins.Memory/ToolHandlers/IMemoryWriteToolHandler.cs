namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆写入和透明化查看工具核心处理器。
/// </summary>
public interface IMemoryWriteToolHandler
{
    /// <summary>用户明确授权时，写入一条人工记忆。</summary>
    string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed);

    /// <summary>查看最近生成或访问的记忆。</summary>
    string ListRecent(int limit, string kind, string workspaceId);
}
