namespace DesktopPet.Rendering.D3D11;

public sealed record D3D11RenderSurface(nint Handle, int Width, int Height) : IRenderSurface;
