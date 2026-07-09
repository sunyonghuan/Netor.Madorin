using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Voice;

namespace Netor.Cortana.Voice;

/// <summary>
/// 语音插件工具调用基础封装。
/// </summary>
public abstract class VoicePluginAdapterBase(
    ILogger logger,
    PluginLoader pluginLoader,
    VoiceCapabilityRegistry registry,
    VoicePluginCapability capability)
{
    public bool IsAvailable => ResolvePlugin(requireEnabled: true) is not null;

    protected async Task<VoicePluginToolResult> InvokeAsync(
        string shortToolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken,
        bool requireEnabled = true)
    {
        var plugin = ResolvePlugin(requireEnabled);
        if (plugin is null)
        {
            logger.LogDebug("未找到可用 {Capability} 插件，跳过工具调用：{ToolName}", capability, shortToolName);
            return VoicePluginToolResult.Skipped($"未找到可用 {capability} 插件。");
        }

        var argsJson = VoicePluginJson.SerializeArgs(args);
        var result = await pluginLoader.InvokePluginToolAsync(
            plugin.PluginId,
            shortToolName,
            argsJson,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(result))
        {
            logger.LogWarning("{Capability} 插件工具无响应：{PluginId}.{ToolName}", capability, plugin.PluginId, shortToolName);
            return VoicePluginToolResult.Failed("empty_response", $"{capability} 插件工具无响应。");
        }

        if (result.StartsWith("[错误]", StringComparison.Ordinal))
        {
            logger.LogWarning("{Capability} 插件工具调用失败：{PluginId}.{ToolName} => {Result}", capability, plugin.PluginId, shortToolName, result);
            return VoicePluginToolResult.Failed("invoke_error", result);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(result, VoiceJsonContext.Default.VoicePluginToolResult);
            if (parsed is not null)
            {
                if (!parsed.Ok)
                {
                    logger.LogWarning(
                        "{Capability} 插件工具返回失败：{PluginId}.{ToolName} => {Code}: {Message}",
                        capability,
                        plugin.PluginId,
                        shortToolName,
                        parsed.Code,
                        parsed.Message);
                }

                return parsed;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "{Capability} 插件工具响应不是结构化 JSON：{PluginId}.{ToolName} => {Result}", capability, plugin.PluginId, shortToolName, result);
        }

        return VoicePluginToolResult.Failed("invalid_response", $"{capability} 插件工具响应格式无效。");
    }

    private VoicePluginDescriptor? ResolvePlugin(bool requireEnabled)
    {
        if (requireEnabled && !registry.IsEnabled(capability))
        {
            return null;
        }

        return registry.GetSelectedPlugin(capability);
    }
}
