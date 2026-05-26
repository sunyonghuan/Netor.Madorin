namespace DesktopPet.Abstractions;

/// <summary>
/// Persisted WebSocket connection settings for the Cortana realtime bridge.
/// </summary>
public sealed record PetConnectionSettings
{
    /// <summary>Cortana host address (hostname or IP). Default: localhost.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Cortana PluginBus WebSocket port. Default: 52841.</summary>
    public int Port { get; init; } = 52841;

    /// <summary>Auto-connect to Cortana on startup.</summary>
    public bool AutoConnect { get; init; } = false;
}
