using System.Numerics;

namespace DesktopPet.Rendering.D3D11;

public sealed record D3D11MeshItem(
    string Id,
    IReadOnlyList<D3D11MeshVertex> Vertices,
    IReadOnlyList<ushort> Indices,
    Matrix4x4 WorldTransform,
    D3D11MeshTexture? Texture = null,
    Vector4 BaseColorFactor = default);
