namespace DesktopPet.Rendering.D3D11;

public sealed record RenderFrameStats(
    long FrameCount,
    double FramesPerSecond,
    string? LastError);
