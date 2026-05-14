
namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆写入、配置和透明化查看工具核心处理器接口。
/// </summary>
public interface IMemoryWriteToolHandler
{
    /// <summary>
    /// 用户明确授权时，写入一条人工记忆。
    /// </summary>
    string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed);

    /// <summary>
    /// 查看最近生成或访问的记忆。
    /// </summary>
    string ListRecent(int limit, string kind, string workspaceId);

    /// <summary>
    /// 查看记忆系统当前所有配置项及中文说明。
    /// </summary>
    string GetSettings(string workspaceId);

    /// <summary>
    /// 修改记忆系统的一项配置。
    /// </summary>
    string UpdateSetting(string settingKey, string settingValue, string reason, string workspaceId, bool userConfirmed);

    /// <summary>
    /// 补齐记忆系统默认配置。
    /// </summary>
    string SeedDefaultSettings(string workspaceId, bool userConfirmed);

    /// <summary>
    /// 删除一条指定的记忆。
    /// </summary>
    string DeleteMemory(string memoryId, string reason, bool userConfirmed);

    /// <summary>
    /// 手动触发一轮记忆片段提取处理。
    /// </summary>
    string TriggerProcessing(int maxCount);
}
