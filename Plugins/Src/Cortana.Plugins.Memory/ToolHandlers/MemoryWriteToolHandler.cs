using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Storage;
using Cortana.Plugins.Memory.Tools;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆写入、配置和透明化查看工具核心处理器。
/// </summary>
public sealed class MemoryWriteToolHandler(
    IMemoryNoteService noteService,
    IMemoryRecentService recentService,
    IMemorySettingsService settingsService,
    IMemoryProcessingService processingService,
    IMemoryStore store,
    IMemoryRuntimeContext runtimeContext,
    ILogger<MemoryWriteToolHandler> logger) : IMemoryWriteToolHandler
{
    private const int MaximumRecentMemoryCount = 50;

    /// <summary>
    /// 所有可配置项的中文说明、默认值和可选范围。
    /// </summary>
    private static readonly Dictionary<string, SettingDescriptor> SettingDescriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["supply.enabled"] = new("记忆供应开关", "true", "bool", "是否在每轮对话前自动注入长期记忆到上下文。关闭后 AI 不会自动获得历史记忆。"),
        ["supply.maxMemoryCount"] = new("最大供应记忆数", "12", "int (1-50)", "每轮对话最多注入多少条长期记忆。数值越大上下文越丰富但消耗更多 Token。"),
        ["recall.maxWindowCount"] = new("召回窗口数", "6", "int (1-20)", "召回时使用的滑动窗口数量，影响召回多样性。"),
        ["recall.maxMemoryCount"] = new("最大召回数", "20", "int (1-50)", "单次召回最多返回的记忆条数。"),
        ["recall.minimumConfidence"] = new("最低召回置信度", "0.35", "double (0-1)", "低于此置信度的记忆不会被召回。提高此值可减少低质量记忆干扰。"),
        ["recall.includeCandidateMemories"] = new("包含候选记忆", "false", "bool", "是否在召回结果中包含尚未确认的候选记忆。"),
        ["decay.enabled"] = new("记忆衰减开关", "true", "bool", "是否启用记忆自然衰减。关闭后所有记忆永不遗忘。"),
        ["decay.defaultRate"] = new("默认衰减速率", "0.015", "double (0-1)", "每轮扫描时记忆保留分数的衰减量。越大遗忘越快。"),
        ["decay.scanIntervalMinutes"] = new("衰减扫描间隔(分钟)", "60", "int (1-1440)", "后台记忆整理的执行间隔，单位分钟。"),
        ["retention.minimumScore"] = new("最低保留分数", "0.2", "double (0-1)", "保留分数低于此值的记忆将被标记为即将遗忘。"),
        ["retention.forgetThreshold"] = new("遗忘阈值", "0.05", "double (0-1)", "保留分数低于此值的记忆将被彻底归档或删除。"),
        ["abstraction.enabled"] = new("抽象记忆生成开关", "true", "bool", "是否启用第三层抽象记忆的自动生成。需要大模型支持。"),
        ["abstraction.minimumSupportCount"] = new("最少支撑片段数", "3", "int (2-20)", "生成一条抽象记忆至少需要多少条相关片段支撑。"),
        ["abstraction.minimumConfidence"] = new("抽象最低置信度", "0.55", "double (0-1)", "参与抽象生成的片段最低置信度要求。"),
        ["governance.auditEnabled"] = new("审计日志开关", "true", "bool", "是否记录所有记忆变更的审计日志。关闭后无法追溯记忆修改历史。")
    };

    /// <inheritdoc />
    public string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed)
    {
        if (string.IsNullOrWhiteSpace(content))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "记忆内容不能为空。");
        if (string.IsNullOrWhiteSpace(reason))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "写入原因不能为空。");
        if (!userConfirmed)
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "写入人工记忆前必须获得用户明确授权。");

        try
        {
            var result = noteService.AddNote(new MemoryAddNoteRequest
            {
                AgentId = runtimeContext.ResolveAgentId(null),
                WorkspaceId = runtimeContext.ResolveWorkspaceId(workspaceId),
                Content = content,
                MemoryType = memoryType,
                Topic = NormalizeOptional(topic),
                Reason = reason,
                UserConfirmed = userConfirmed,
                TriggerSource = "memory_add_note_tool",
                TraceId = Guid.NewGuid().ToString("N")
            });

            return MemoryToolResult.Ok("人工记忆写入成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Chinese.MemoryAddNoteResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "人工记忆写入工具调用失败。Type={MemoryType}", memoryType);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "人工记忆写入工具调用异常。Type={MemoryType}", memoryType);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"人工记忆写入失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string ListRecent(int limit, string kind, string workspaceId)
    {
        try
        {
            var result = recentService.ListRecent(new MemoryListRecentRequest
            {
                AgentId = runtimeContext.ResolveAgentId(null),
                WorkspaceId = runtimeContext.ResolveWorkspaceId(workspaceId),
                Limit = limit <= 0 ? null : Math.Min(limit, MaximumRecentMemoryCount),
                Kind = NormalizeOptional(kind)
            });

            return MemoryToolResult.Ok("最近记忆读取成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Chinese.MemoryListRecentResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "最近记忆列表工具调用失败。Kind={Kind}", kind);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "最近记忆列表工具调用异常。Kind={Kind}", kind);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"最近记忆读取失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string GetSettings(string workspaceId)
    {
        try
        {
            var agentId = runtimeContext.ResolveAgentId(null);
            var ws = NormalizeOptional(workspaceId);
            var items = new List<MemorySettingItem>();

            foreach (var (key, descriptor) in SettingDescriptors)
            {
                var currentValue = settingsService.GetString(key, descriptor.DefaultValue, agentId, ws);
                items.Add(new MemorySettingItem
                {
                    Key = key,
                    DisplayName = descriptor.DisplayName,
                    Description = descriptor.Description,
                    CurrentValue = currentValue,
                    DefaultValue = descriptor.DefaultValue,
                    ValueType = descriptor.ValueType
                });
            }

            var result = new MemorySettingsResult
            {
                Count = items.Count,
                Items = items
            };

            return MemoryToolResult.Ok("记忆系统配置读取成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Chinese.MemorySettingsResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆配置读取失败。");
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"配置读取失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string UpdateSetting(string settingKey, string settingValue, string reason, string workspaceId, bool userConfirmed)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "配置键不能为空。");
        if (!userConfirmed)
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "修改配置前必须获得用户明确授权。");
        if (!SettingDescriptors.ContainsKey(settingKey))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, $"不支持的配置键: {settingKey}。请使用 memory_get_settings 查看可用配置。");

        try
        {
            var agentId = runtimeContext.ResolveAgentId(null);
            var ws = NormalizeOptional(workspaceId);

            store.UpsertMemorySetting(new MemorySetting
            {
                SettingKey = settingKey.Trim(),
                SettingValue = settingValue?.Trim() ?? string.Empty,
                AgentId = agentId,
                WorkspaceId = ws,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

            store.InsertMemoryMutation(new MemoryMutation
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                MemoryId = $"setting:{settingKey}",
                MemoryKind = "setting",
                MutationType = "update",
                BeforeJson = null,
                AfterJson = $"{{\"key\":\"{settingKey}\",\"value\":\"{settingValue}\"}}",
                Reason = reason ?? "用户通过工具修改配置。",
                TraceId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

            var descriptor = SettingDescriptors[settingKey];
            return MemoryToolResult.Ok($"配置已更新：{descriptor.DisplayName} = {settingValue}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆配置修改失败。Key={Key}", settingKey);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"配置修改失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string DeleteMemory(string memoryId, string reason, bool userConfirmed)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "记忆 ID 不能为空。");
        if (!userConfirmed)
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "删除记忆前必须获得用户明确授权。");

        try
        {
            var agentId = runtimeContext.ResolveAgentId(null);

            // 按 ID 精确查找
            var target = store.GetFragmentById(memoryId);
            if (target is null)
                return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, $"未找到 ID 为 {memoryId} 的记忆。");

            target.LifecycleState = "archived";
            target.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            store.UpsertMemoryFragment(target);

            store.InsertMemoryMutation(new MemoryMutation
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                MemoryId = memoryId,
                MemoryKind = "fragment",
                MutationType = "archive",
                BeforeJson = null,
                AfterJson = null,
                Reason = reason ?? "用户通过工具删除记忆。",
                TraceId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

            return MemoryToolResult.Ok($"记忆已归档删除：{target.Title ?? target.Summary?[..Math.Min(40, target.Summary.Length)]}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆删除失败。MemoryId={MemoryId}", memoryId);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"记忆删除失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string TriggerProcessing(int maxCount)
    {
        try
        {
            var count = maxCount <= 0 ? 100 : Math.Min(maxCount, 500);

            // 异步触发，不阻塞等待完成——处理大量 observations 可能耗时很长
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await processingService.ProcessAsync(new MemoryProcessingRequest
                    {
                        MaxObservationCount = count,
                        TriggerSource = "memory_trigger_processing_tool",
                        TraceId = Guid.NewGuid().ToString("N")
                    }, CancellationToken.None);

                    logger.LogInformation(
                        "手动触发记忆处理完成：处理 {Processed} 条，生成 {Created} 条片段，合并 {Merged} 条。",
                        result.ProcessedObservationCount, result.CreatedFragmentCount, result.MergedFragmentCount);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "手动触发记忆处理后台执行失败。");
                }
            });

            return MemoryToolResult.Ok(
                $"记忆处理已在后台触发，最多处理 {count} 条观察记录。处理需要时间，请稍后使用 memory_get_status 或 memory_list_recent 查看结果。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "手动触发记忆处理失败。");
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"记忆处理触发失败: {ex.Message}");
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record SettingDescriptor(string DisplayName, string DefaultValue, string ValueType, string Description);
}
