namespace DesktopPet.Models.Live2D;

public sealed record Live2DDrawableSnapshot(
    int Index,
    string Id,
    int TextureIndex,
    string ParentPartId,
    float ParentPartOpacity,
    int DrawOrder,
    int RenderOrder,
    int BlendMode,
    bool IsDoubleSided,
    bool IsInvertedMask,
    bool IsVisible,
    int VertexCount,
    int IndexCount,
    float Opacity,
    IReadOnlyList<float> VertexPositions,
    IReadOnlyList<float> VertexUvs,
    IReadOnlyList<ushort> Indices,
    IReadOnlyList<int> MaskIndices);
