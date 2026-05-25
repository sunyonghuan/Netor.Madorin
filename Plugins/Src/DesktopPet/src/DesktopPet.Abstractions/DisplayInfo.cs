namespace DesktopPet.Abstractions;

/// <summary>
/// Display bounds used to validate and restore window placement.
/// </summary>
public readonly record struct DisplayInfo(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);
