using Cortana.Plugins.Memory.ToolHandlers;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Memory.Tools;

/// <summary>
/// 记忆插件 P1 写入、配置和透明化查看工具。
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

    /// <summary>查看记忆系统当前所有配置项及中文说明。</summary>
    [Tool(Name = "memory_get_settings",
        Description = "查看记忆系统当前所有配置项，返回配置键、当前值、默认值、中文说明和可选范围。用于了解和调整记忆系统行为。")]
    public string GetSettings(
        [Parameter(Description = "工作区标识，空字符串表示查看全局配置")] string workspaceId)
    {
        return handler.GetSettings(workspaceId);
    }

    /// <summary>修改记忆系统的一项配置。</summary>
    [Tool(Name = "memory_update_setting",
        Description = "修改记忆系统的一项配置。需要用户明确授权。修改后立即生效，影响记忆召回、供应、衰减和抽象生成行为。")]
    public string UpdateSetting(
        [Parameter(Description = "配置键，例如 supply.enabled、recall.maxMemoryCount")] string settingKey,
        [Parameter(Description = "新的配置值")] string settingValue,
        [Parameter(Description = "修改原因")] string reason,
        [Parameter(Description = "工作区标识，空字符串表示修改全局配置")] string workspaceId,
        [Parameter(Description = "是否已获得用户明确授权，必须为 true")] bool userConfirmed)
    {
        return handler.UpdateSetting(settingKey, settingValue, reason, workspaceId, userConfirmed);
    }

    /// <summary>补齐记忆系统默认配置。</summary>
    [Tool(Name = "memory_seed_default_settings",
        Description = "补齐记忆插件默认配置种子，包含分层主动注入、召回、供应、衰减、抽象和治理配置。需要用户明确授权，不覆盖已有配置。")]
    public string SeedDefaultSettings(
        [Parameter(Description = "工作区标识，空字符串表示补齐全局配置")] string workspaceId,
        [Parameter(Description = "是否已获得用户明确授权，必须为 true")] bool userConfirmed)
    {
        return handler.SeedDefaultSettings(workspaceId, userConfirmed);
    }

    /// <summary>删除一条指定的记忆。</summary>
    [Tool(Name = "memory_delete",
        Description = "删除一条指定的长期记忆。需要用户明确授权。删除后不可恢复，但会记录审计日志。")]
    public string DeleteMemory(
        [Parameter(Description = "要删除的记忆 ID")] string memoryId,
        [Parameter(Description = "删除原因")] string reason,
        [Parameter(Description = "是否已获得用户明确授权，必须为 true")] bool userConfirmed)
    {
        return handler.DeleteMemory(memoryId, reason, userConfirmed);
    }

    /// <summary>手动触发一轮记忆片段提取处理。</summary>
    [Tool(Name = "memory_trigger_processing",
        Description = "手动触发一轮记忆片段提取处理。将未处理的观察记录提取为记忆片段。通常用于测试或调试，正常情况下系统会自动定时处理。")]
    public string TriggerProcessing(
        [Parameter(Description = "最大处理观察记录数，0 表示默认 100")] int maxCount)
    {
        return handler.TriggerProcessing(maxCount);
    }
}
