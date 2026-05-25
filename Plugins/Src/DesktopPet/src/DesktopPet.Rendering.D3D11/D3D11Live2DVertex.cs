using System.Runtime.InteropServices;

namespace DesktopPet.Rendering.D3D11;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct D3D11Live2DVertex
{
    public D3D11Live2DVertex(float x, float y, float u, float v, float opacity)
    {
        X = x;
        Y = y;
        U = u;
        V = v;
        Opacity = opacity;
    }

    public readonly float X;

    public readonly float Y;

    public readonly float U;

    public readonly float V;

    public readonly float Opacity;
}
