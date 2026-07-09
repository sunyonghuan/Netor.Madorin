using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Voice;

namespace Netor.Cortana.Voice;

/// <summary>
/// 封装 TTS 语音插件工具调用。
/// </summary>
public sealed class TtsPluginAdapter(
    ILogger<TtsPluginAdapter> logger,
    PluginLoader pluginLoader,
    VoiceCapabilityRegistry registry,
    SystemSettingsService settings)
    : ITtsPluginAdapter
{
    public bool IsAvailable => ResolvePlugin() is not null;

    public Task EnqueueAsync(string text, string? sessionId, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_enqueue",
            new Dictionary<string, object?>
            {
                ["text"] = text,
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken);

    public Task FinishAsync(string? sessionId, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_finish",
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken);

    public Task StopAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_stop",
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken,
            requireEnabled: false);

    public Task<TtsPluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_configure",
            new Dictionary<string, object?>
            {
                ["speed"] = settings.GetValue("Tts.Speed", 1.0f),
                ["voice"] = string.Empty,
                ["model_directory"] = string.Empty
            },
            cancellationToken);

    public Task<TtsPluginToolResult> RegenerateGreetingAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_greeting_regen",
            new Dictionary<string, object?>
            {
                ["greeting"] = settings.GetValue("Tts.WelcomeGreeting", "主人，我在!")
            },
            cancellationToken);

    public Task<TtsPluginToolResult> GreetingPlayAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_tts_greeting_play",
            new Dictionary<string, object?>
            {
                ["greeting"] = string.Empty,
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken);

    private async Task<TtsPluginToolResult> InvokeAsync(
        string shortToolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken,
        bool requireEnabled = true)
    {
        var plugin = ResolvePlugin(requireEnabled);
        if (plugin is null)
        {
            logger.LogDebug("未找到可用 TTS 插件，跳过工具调用：{ToolName}", shortToolName);
            return TtsPluginToolResult.Skipped("未找到可用 TTS 插件。");
        }

        var argsJson = VoicePluginJson.SerializeArgs(args);
        var result = await pluginLoader.InvokePluginToolAsync(
            plugin.PluginId,
            shortToolName,
            argsJson,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(result))
        {
            logger.LogWarning("TTS 插件工具无响应：{PluginId}.{ToolName}", plugin.PluginId, shortToolName);
            return TtsPluginToolResult.Failed("empty_response", "TTS 插件工具无响应。");
        }

        if (result.StartsWith("[错误]", StringComparison.Ordinal))
        {
            logger.LogWarning("TTS 插件工具调用失败：{PluginId}.{ToolName} => {Result}", plugin.PluginId, shortToolName, result);
            return TtsPluginToolResult.Failed("invoke_error", result);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(result, VoiceJsonContext.Default.TtsPluginToolResult);
            if (parsed is not null)
            {
                if (!parsed.Ok)
                {
                    logger.LogWarning(
                        "TTS 插件工具返回失败：{PluginId}.{ToolName} => {Code}: {Message}",
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
            logger.LogWarning(ex, "TTS 插件工具响应不是结构化 JSON：{PluginId}.{ToolName} => {Result}", plugin.PluginId, shortToolName, result);
        }

        return TtsPluginToolResult.Failed("invalid_response", "TTS 插件工具响应格式无效。");
    }

    private VoicePluginDescriptor? ResolvePlugin(bool requireEnabled = true)
    {
        if (requireEnabled && !registry.IsEnabled(VoicePluginCapability.Tts))
        {
            return null;
        }

        return registry.GetSelectedPlugin(VoicePluginCapability.Tts);
    }
}
