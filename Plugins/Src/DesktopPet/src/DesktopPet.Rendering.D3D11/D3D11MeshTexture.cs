using System.Numerics;

namespace DesktopPet.Rendering.D3D11;

public sealed record D3D11MeshTexture(
    string CacheKey,
    int Width,
    int Height,
    byte[] RgbaPixels,
    Vector4 BaseColorFactor);
