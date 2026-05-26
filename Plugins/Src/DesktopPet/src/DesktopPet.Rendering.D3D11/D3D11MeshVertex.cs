using System.Runtime.InteropServices;

namespace DesktopPet.Rendering.D3D11;

[StructLayout(LayoutKind.Sequential)]
public readonly struct D3D11MeshVertex
{
    public D3D11MeshVertex(
        float x, float y, float z,
        float r, float g, float b, float a,
        float u = 0f, float v = 0f,
        ushort j0 = 0, ushort j1 = 0, ushort j2 = 0, ushort j3 = 0,
        float w0 = 1f, float w1 = 0f, float w2 = 0f, float w3 = 0f)
    {
        X = x; Y = y; Z = z;
        R = r; G = g; B = b; A = a;
        U = u; V = v;
        J0 = j0; J1 = j1; J2 = j2; J3 = j3;
        W0 = w0; W1 = w1; W2 = w2; W3 = w3;
    }

    // Position (12 bytes)
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    // Normal/Color (16 bytes)
    public readonly float R;
    public readonly float G;
    public readonly float B;
    public readonly float A;

    // TexCoord (8 bytes)
    public readonly float U;
    public readonly float V;

    // Joint indices, packed as 4 × uint16 (8 bytes total)
    public readonly ushort J0;
    public readonly ushort J1;
    public readonly ushort J2;
    public readonly ushort J3;

    // Blend weights (16 bytes)
    public readonly float W0;
    public readonly float W1;
    public readonly float W2;
    public readonly float W3;
    // Total: 60 bytes
}

