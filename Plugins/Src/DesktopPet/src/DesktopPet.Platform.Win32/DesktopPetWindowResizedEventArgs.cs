namespace DesktopPet.Platform.Win32;

public sealed class DesktopPetWindowResizedEventArgs : EventArgs
{
    public DesktopPetWindowResizedEventArgs(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}
