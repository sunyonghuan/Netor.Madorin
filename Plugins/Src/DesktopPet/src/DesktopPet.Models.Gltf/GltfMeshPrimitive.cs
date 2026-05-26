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

    // ── Skinning (Linear Blend Skinning) ──────────────────────────────────────

    /// <summary>
    /// JOINTS_0: 4 joint indices per vertex, stored flat as [v0j0, v0j1, v0j2, v0j3, v1j0, ...].
    /// Length == VertexCount * 4. Null when the mesh is not skinned.
    /// </summary>
    public ushort[]? JointIndices { get; init; }

    /// <summary>
    /// WEIGHTS_0: 4 blend weights per vertex, stored flat. Length == VertexCount * 4.
    /// Null when the mesh is not skinned.
    /// </summary>
    public float[]? Weights { get; init; }

    /// <summary>Index into the glTF skins array that governs this primitive's skeleton.</summary>
    public int SkinIndex { get; init; } = -1;

    /// <summary>True when this primitive has valid joint/weight data for GPU skinning.</summary>
    public bool IsSkinned => JointIndices is not null && Weights is not null && SkinIndex >= 0;
}
