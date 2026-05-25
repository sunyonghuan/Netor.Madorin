namespace DesktopPet.Rendering.D3D11;

public interface IRenderSurface
{
    nint Handle { get; }

    int Width { get; }

    int Height { get; }
}
