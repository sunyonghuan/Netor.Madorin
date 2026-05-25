namespace DesktopPet.Abstractions;

/// <summary>
/// Root application settings for DesktopPet.
/// </summary>
public sealed record DesktopPetSettings
{
    public PetWindowPlacement Window { get; init; } = new();
}
