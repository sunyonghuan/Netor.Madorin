using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DesktopPet.Models.Gltf;

internal static class GltfMeshExtractor
{
    private const int ComponentTypeUnsignedShort = 5123;
    private const int ComponentTypeUnsignedInt = 5125;
    private const int ComponentTypeFloat = 5126;

    public static IReadOnlyList<GltfMeshPrimitive> Extract(GltfManifest manifest, byte[] binaryChunk)
    {
        var meshes = manifest.Meshes;
        var accessors = manifest.Accessors;
        var bufferViews = manifest.BufferViews;

        if (meshes is null || meshes.Count == 0 || accessors is null || bufferViews is null)
        {
            return [];
        }

        var result = new List<GltfMeshPrimitive>();

        // Build node-to-mesh mapping by walking the scene graph so each
        // primitive knows which node it belongs to (needed for world transforms).
        var nodeMeshPairs = BuildNodeMeshPairs(manifest);

        foreach (var (nodeIndex, meshIndex) in nodeMeshPairs)
        {
            var mesh = meshes[meshIndex];
            var primitives = mesh.Primitives;
            if (primitives is null)
            {
                continue;
            }

            for (var pi = 0; pi < primitives.Count; pi++)
            {
                var prim = primitives[pi];
                var attributes = prim.Attributes;
                if (attributes is null || !attributes.TryGetValue("POSITION", out var posAccessorIndex))
                {
                    continue;
                }

                var positions = ReadFloat3Accessor(accessors, bufferViews, binaryChunk, posAccessorIndex);
                if (positions is null || positions.Length == 0)
                {
                    continue;
                }

                float[]? normals = null;
                if (attributes.TryGetValue("NORMAL", out var normalAccessorIndex))
                {
                    normals = ReadFloat3Accessor(accessors, bufferViews, binaryChunk, normalAccessorIndex);
                }

                float[]? texCoords = null;
                if (attributes.TryGetValue("TEXCOORD_0", out var texCoordAccessorIndex))
                {
                    texCoords = ReadFloat2Accessor(accessors, bufferViews, binaryChunk, texCoordAccessorIndex);
                }

                ushort[]? indices = null;
                if (prim.Indices.HasValue)
                {
                    indices = ReadIndicesAccessor(accessors, bufferViews, binaryChunk, prim.Indices.Value);
                }

                indices ??= GenerateSequentialIndices(positions.Length / 3);

                IReadOnlyList<float[]>? morphDeltas = null;
                if (prim.Targets is { Count: > 0 })
                {
                    var deltas = new float[prim.Targets.Count][];
                    for (var t = 0; t < prim.Targets.Count; t++)
                    {
                        deltas[t] = prim.Targets[t].TryGetValue("POSITION", out var morphPosIdx)
                            ? ReadFloat3Accessor(accessors, bufferViews, binaryChunk, morphPosIdx) ?? []
                            : [];
                    }
                    morphDeltas = deltas;
                }

                result.Add(new GltfMeshPrimitive(
                    mesh.Name ?? $"Mesh{result.Count}",
                    pi,
                    nodeIndex,
                    positions,
                    normals,
                    texCoords,
                    indices,
                    prim.Material)
                {
                    MorphPositionDeltas = morphDeltas
                });
            }
        }

        return result;
    }

    private static List<(int NodeIndex, int MeshIndex)> BuildNodeMeshPairs(GltfManifest manifest)
    {
        var nodes = manifest.Nodes;
        var scenes = manifest.Scenes;
        var result = new List<(int, int)>();

        if (nodes is null || scenes is null || scenes.Count == 0)
        {
            // Fall back: enumerate all nodes regardless of scene
            if (nodes is not null)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Mesh.HasValue)
                    {
                        result.Add((i, nodes[i].Mesh!.Value));
                    }
                }
            }
            return result;
        }

        var sceneIndex = manifest.Scene ?? 0;
        if (sceneIndex >= scenes.Count)
        {
            sceneIndex = 0;
        }

        var rootNodes = scenes[sceneIndex].Nodes;
        if (rootNodes is null)
        {
            return result;
        }

        var visited = new HashSet<int>();
        foreach (var rootNodeIndex in rootNodes)
        {
            WalkNode(nodes, rootNodeIndex, visited, result);
        }

        return result;
    }

    private static void WalkNode(
        IReadOnlyList<GltfNode> nodes,
        int nodeIndex,
        HashSet<int> visited,
        List<(int NodeIndex, int MeshIndex)> result)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count || !visited.Add(nodeIndex))
        {
            return;
        }

        var node = nodes[nodeIndex];
        if (node.Mesh.HasValue)
        {
            result.Add((nodeIndex, node.Mesh.Value));
        }

        if (node.Children is not null)
        {
            foreach (var childIndex in node.Children)
            {
                WalkNode(nodes, childIndex, visited, result);
            }
        }
    }

    private static float[]? ReadFloat3Accessor(
        IReadOnlyList<GltfAccessor> accessors,
        IReadOnlyList<GltfBufferView> bufferViews,
        byte[] binaryChunk,
        int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
        {
            return null;
        }

        var accessor = accessors[accessorIndex];
        if (accessor.ComponentType != ComponentTypeFloat || accessor.Type != "VEC3")
        {
            return null;
        }

        var span = GetAccessorSpan(accessor, bufferViews, binaryChunk);
        if (span.IsEmpty)
        {
            return null;
        }

        var result = new float[accessor.Count * 3];
        var stride = GetStride(accessor, bufferViews, 12);
        for (var i = 0; i < accessor.Count; i++)
        {
            var offset = i * stride;
            result[i * 3 + 0] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4));
            result[i * 3 + 1] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
            result[i * 3 + 2] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4));
        }

        return result;
    }

    private static float[]? ReadFloat2Accessor(
        IReadOnlyList<GltfAccessor> accessors,
        IReadOnlyList<GltfBufferView> bufferViews,
        byte[] binaryChunk,
        int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
        {
            return null;
        }

        var accessor = accessors[accessorIndex];
        if (accessor.ComponentType != ComponentTypeFloat || accessor.Type != "VEC2")
        {
            return null;
        }

        var span = GetAccessorSpan(accessor, bufferViews, binaryChunk);
        if (span.IsEmpty)
        {
            return null;
        }

        var result = new float[accessor.Count * 2];
        var stride = GetStride(accessor, bufferViews, 8);
        for (var i = 0; i < accessor.Count; i++)
        {
            var offset = i * stride;
            result[i * 2 + 0] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4));
            result[i * 2 + 1] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
        }

        return result;
    }

    private static ushort[]? ReadIndicesAccessor(
        IReadOnlyList<GltfAccessor> accessors,
        IReadOnlyList<GltfBufferView> bufferViews,
        byte[] binaryChunk,
        int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
        {
            return null;
        }

        var accessor = accessors[accessorIndex];
        if (accessor.Type != "SCALAR")
        {
            return null;
        }

        var span = GetAccessorSpan(accessor, bufferViews, binaryChunk);
        if (span.IsEmpty)
        {
            return null;
        }

        var result = new ushort[accessor.Count];
        if (accessor.ComponentType == ComponentTypeUnsignedShort)
        {
            var stride = GetStride(accessor, bufferViews, 2);
            for (var i = 0; i < accessor.Count; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * stride, 2));
            }
        }
        else if (accessor.ComponentType == ComponentTypeUnsignedInt)
        {
            var stride = GetStride(accessor, bufferViews, 4);
            for (var i = 0; i < accessor.Count; i++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i * stride, 4));
                result[i] = checked((ushort)value);
            }
        }
        else
        {
            return null;
        }

        return result;
    }

    private static ReadOnlySpan<byte> GetAccessorSpan(
        GltfAccessor accessor,
        IReadOnlyList<GltfBufferView> bufferViews,
        byte[] binaryChunk)
    {
        if (!accessor.BufferView.HasValue)
        {
            return [];
        }

        var bvIndex = accessor.BufferView.Value;
        if (bvIndex < 0 || bvIndex >= bufferViews.Count)
        {
            return [];
        }

        var bv = bufferViews[bvIndex];
        var byteOffset = (bv.ByteOffset ?? 0) + (accessor.ByteOffset ?? 0);
        var byteLength = bv.ByteLength;

        if (byteOffset + byteLength > binaryChunk.Length)
        {
            return [];
        }

        return binaryChunk.AsSpan(byteOffset, byteLength);
    }

    private static int GetStride(GltfAccessor accessor, IReadOnlyList<GltfBufferView> bufferViews, int elementSize)
    {
        if (!accessor.BufferView.HasValue)
        {
            return elementSize;
        }

        var bvIndex = accessor.BufferView.Value;
        if (bvIndex < 0 || bvIndex >= bufferViews.Count)
        {
            return elementSize;
        }

        return bufferViews[bvIndex].ByteStride ?? elementSize;
    }

    private static ushort[] GenerateSequentialIndices(int vertexCount)
    {
        var indices = new ushort[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            indices[i] = checked((ushort)i);
        }

        return indices;
    }
}
