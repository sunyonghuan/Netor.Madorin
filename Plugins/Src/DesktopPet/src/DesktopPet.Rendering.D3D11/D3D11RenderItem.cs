namespace DesktopPet.Rendering.D3D11;

public sealed record D3D11RenderItem(
    int DrawableIndex,
    string Id,
    int TextureIndex,
    string? TexturePath,
    int DrawOrder,
    int RenderOrder,
    int BlendMode,
    bool IsDoubleSided,
    bool IsInvertedMask,
    bool IsVisible,
    float Opacity,
    IReadOnlyList<float> VertexPositions,
    IReadOnlyList<float> VertexUvs,
    IReadOnlyList<ushort> Indices,
    IReadOnlyList<int> MaskIndices);
