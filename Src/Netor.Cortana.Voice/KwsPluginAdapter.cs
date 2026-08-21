using Microsoft.Extensions.Logging;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.Voice;

namespace Netor.Cortana.Voice;

/// <summary>
/// 封装 KWS 语音唤醒插件工具调用。
/// </summary>
public sealed class KwsPluginAdapter(
    ILogger<KwsPluginAdapter> logger,
    PluginLoader pluginLoader,
    VoiceCapabilityRegistry registry,
    SystemSettingsService settings)
    : VoicePluginAdapterBase(logger, pluginLoader, registry, VoicePluginCapability.Kws), IKwsPluginAdapter
{
    public Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(
            "voice_kws_configure",
            new Dictionary<string, object?>
            {
                ["wake_phrase"] = settings.GetValue("Voice.WakeWord", "小娜"),
                ["model_directory"] = settings.GetValue("Voice.Kws.ModelDirectory", string.Empty),
                ["keywords_threshold"] = settings.GetValue("SherpaOnnx.KeywordsThreshold", 0.1f),
                ["keywords_score"] = settings.GetValue("SherpaOnnx.KeywordsScore", 2.0f),
                ["num_trailing_blanks"] = settings.GetValue("SherpaOnnx.NumTrailingBlanks", 1)
            },
            cancellationToken);

    public Task<VoicePluginToolResult> StartAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("voice_kws_start", new Dictionary<string, object?>(), cancellationToken);

    public Task<VoicePluginToolResult> StopAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("voice_kws_stop", new Dictionary<string, object?>(), cancellationToken, requireEnabled: false);
}
