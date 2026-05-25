using System.Runtime.InteropServices;

namespace DesktopPet.Rendering.D3D11;

[StructLayout(LayoutKind.Sequential)]
public readonly struct D3D11MeshVertex
{
    public D3D11MeshVertex(float x, float y, float z, float r, float g, float b, float a, float u = 0f, float v = 0f)
    {
        X = x;
        Y = y;
        Z = z;
        R = r;
        G = g;
        B = b;
        A = a;
        U = u;
        V = v;
    }

    public readonly float X;

    public readonly float Y;

    public readonly float Z;

    public readonly float R;

    public readonly float G;

    public readonly float B;

    public readonly float A;

    public readonly float U;

    public readonly float V;
}
