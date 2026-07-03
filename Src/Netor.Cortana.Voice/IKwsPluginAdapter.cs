namespace Netor.Cortana.Voice;

public interface IKwsPluginAdapter
{
    Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default);

    Task<VoicePluginToolResult> StartAsync(CancellationToken cancellationToken = default);

    Task<VoicePluginToolResult> StopAsync(CancellationToken cancellationToken = default);
}