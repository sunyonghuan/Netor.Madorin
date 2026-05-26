namespace DesktopPet.Abstractions;

/// <summary>
/// Root application settings for DesktopPet.
/// </summary>
public sealed record DesktopPetSettings
{
    public PetWindowPlacement Window { get; init; } = new();

    /// <summary>WebSocket connection settings for the Cortana realtime bridge.</summary>
    public PetConnectionSettings Connection { get; init; } = new();
}
