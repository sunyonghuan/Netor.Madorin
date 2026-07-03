namespace Netor.Cortana.Voice;

public interface ISttPluginAdapter
{
    Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default);

    Task<VoicePluginToolResult> StartAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    Task<VoicePluginToolResult> StopAsync(string? sessionId = null, CancellationToken cancellationToken = default);
}