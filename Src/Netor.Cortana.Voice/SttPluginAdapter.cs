using Microsoft.Extensions.Logging;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Voice;

namespace Netor.Cortana.Voice;

/// <summary>
/// 封装 STT 语音识别插件工具调用。
/// </summary>
public sealed class SttPluginAdapter(
    ILogger<SttPluginAdapter> logger,
    PluginLoader pluginLoader,
    VoiceCapabilityRegistry registry,
    SystemSettingsService settings)
    : VoicePluginAdapterBase(logger, pluginLoader, registry, VoicePluginCapability.Stt), ISttPluginAdapter
{
    public Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_stt_configure",
            new Dictionary<string, object?>
            {
                ["model_directory"] = settings.GetValue("Voice.Stt.ModelDirectory", string.Empty),
                ["rule1_min_trailing_silence"] = settings.GetValue("SherpaOnnx.Rule1MinTrailingSilence", 2.4f),
                ["rule2_min_trailing_silence"] = settings.GetValue("SherpaOnnx.Rule2MinTrailingSilence", 1.2f),
                ["rule3_min_utterance_length"] = settings.GetValue("SherpaOnnx.Rule3MinUtteranceLength", 20.0f),
                ["recognition_timeout_seconds"] = settings.GetValue("SherpaOnnx.RecognitionTimeoutSeconds", 10.0f),
                ["num_threads"] = settings.GetValue("SherpaOnnx.NumThreads", 2)
            },
            cancellationToken);

    public Task<VoicePluginToolResult> StartAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_stt_start",
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken);

    public Task<VoicePluginToolResult> StopAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_stt_stop",
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId ?? string.Empty
            },
            cancellationToken,
            requireEnabled: false);
}
