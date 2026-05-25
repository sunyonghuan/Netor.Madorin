using System.Numerics;
using System.Runtime.InteropServices;

namespace DesktopPet.Rendering.D3D11;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct D3D11MeshConstants : IEquatable<D3D11MeshConstants>
{
    public D3D11MeshConstants(Matrix4x4 worldViewProjection, Vector4 baseColorFactor, float hasTexture)
    {
        WorldViewProjection = worldViewProjection;
        BaseColorFactor = baseColorFactor;
        HasTexture = hasTexture;
        _pad0 = 0f;
        _pad1 = 0f;
        _pad2 = 0f;
    }

    public readonly Matrix4x4 WorldViewProjection;  // 64 bytes

    public readonly Vector4 BaseColorFactor;          // 16 bytes

    public readonly float HasTexture;                 // 4 bytes

    private readonly float _pad0;                     // pad to 16-byte boundary

    private readonly float _pad1;

    private readonly float _pad2;

    public bool Equals(D3D11MeshConstants other)
        => WorldViewProjection == other.WorldViewProjection
        && BaseColorFactor == other.BaseColorFactor
        && HasTexture == other.HasTexture;

    public override bool Equals(object? obj) => obj is D3D11MeshConstants other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(WorldViewProjection, BaseColorFactor, HasTexture);

    public static bool operator ==(D3D11MeshConstants left, D3D11MeshConstants right) => left.Equals(right);
    public static bool operator !=(D3D11MeshConstants left, D3D11MeshConstants right) => !left.Equals(right);
}
