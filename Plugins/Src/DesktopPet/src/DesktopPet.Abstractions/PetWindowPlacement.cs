namespace DesktopPet.Abstractions;

/// <summary>
/// Persisted desktop pet window placement and display behavior.
/// </summary>
public sealed record PetWindowPlacement
{
    public string? MonitorDeviceName { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; } = 420;

    public int Height { get; init; } = 560;

    public double Scale { get; init; } = 1.0;

    public double Opacity { get; init; } = 1.0;

    public bool TopMost { get; init; } = true;

    public bool ClickThrough { get; init; }

    public bool Locked { get; init; }

    public bool RememberPlacement { get; init; } = true;
}
