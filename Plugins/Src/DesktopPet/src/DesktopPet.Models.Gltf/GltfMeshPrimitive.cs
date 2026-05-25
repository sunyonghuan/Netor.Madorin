namespace DesktopPet.Models.Gltf;

public sealed record GltfMeshPrimitive(
    string MeshName,
    int PrimitiveIndex,
    int NodeIndex,
    float[] Positions,
    float[]? Normals,
    float[]? TexCoords,
    ushort[] Indices,
    int? MaterialIndex)
{
    public int VertexCount => Positions.Length / 3;

    /// <summary>Per-morph-target POSITION deltas; index matches <c>mesh.primitives[i].targets</c>.</summary>
    public IReadOnlyList<float[]>? MorphPositionDeltas { get; init; }
}
