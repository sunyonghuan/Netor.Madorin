namespace Netor.Cortana.Voice;

public interface ITtsPluginAdapter
{
    bool IsAvailable { get; }

    Task EnqueueAsync(string text, string? sessionId, CancellationToken cancellationToken = default);

    Task FinishAsync(string? sessionId, CancellationToken cancellationToken = default);

    Task<TtsPluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default);

    Task<TtsPluginToolResult> RegenerateGreetingAsync(CancellationToken cancellationToken = default);

    Task<TtsPluginToolResult> GreetingPlayAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    Task StopAsync(string? sessionId = null, CancellationToken cancellationToken = default);
}