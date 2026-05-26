using System.Numerics;

namespace DesktopPet.Models.Gltf;

internal static class GltfNodeTransformTree
{
    public static Dictionary<int, Matrix4x4> ComputeWorldMatrices(
        GltfManifest manifest,
        IReadOnlyDictionary<int, GltfNodeOverride>? overrides = null)
    {
        var result = new Dictionary<int, Matrix4x4>();
        var nodes = manifest.Nodes;
        var scenes = manifest.Scenes;

        if (nodes is null || scenes is null || scenes.Count == 0)
        {
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
        foreach (var rootIndex in rootNodes)
        {
            WalkNode(nodes, rootIndex, Matrix4x4.Identity, overrides, visited, result);
        }

        return result;
    }

    private static void WalkNode(
        IReadOnlyList<GltfNode> nodes,
        int nodeIndex,
        Matrix4x4 parentWorld,
        IReadOnlyDictionary<int, GltfNodeOverride>? overrides,
        HashSet<int> visited,
        Dictionary<int, Matrix4x4> result)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count || !visited.Add(nodeIndex))
        {
            return;
        }

        var node = nodes[nodeIndex];
        GltfNodeOverride? nodeOverride = null;
        overrides?.TryGetValue(nodeIndex, out nodeOverride);
        var local = ComputeLocalMatrix(node, nodeOverride);
        var world = local * parentWorld;
        result[nodeIndex] = world;

        if (node.Children is not null)
        {
            foreach (var childIndex in node.Children)
            {
                WalkNode(nodes, childIndex, world, overrides, visited, result);
            }
        }
    }

    private static Matrix4x4 ComputeLocalMatrix(GltfNode node, GltfNodeOverride? nodeOverride)
    {
        // If a raw matrix is set and no animation override, use it directly.
        // glTF node.matrix is 16 floats stored column-major.
        // System.Numerics uses row-vector convention (v' = v * M), where the Matrix4x4
        // constructor fills elements row-by-row.  Reading the column-major glTF data
        // directly into the constructor (no explicit transpose) produces the correct
        // row-vector matrix — the implicit reinterpretation acts as a transpose.
        if (nodeOverride is null && node.Matrix is { Length: 16 } m)
        {
            return new Matrix4x4(
                m[0],  m[1],  m[2],  m[3],
                m[4],  m[5],  m[6],  m[7],
                m[8],  m[9],  m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        var t = nodeOverride?.Translation ?? ParseVec3(node.Translation) ?? Vector3.Zero;
        var r = nodeOverride?.Rotation ?? ParseQuat(node.Rotation) ?? Quaternion.Identity;
        var s = nodeOverride?.Scale ?? ParseVec3(node.Scale) ?? Vector3.One;

        // glTF TRS convention: M = T * R * S
        return Matrix4x4.CreateScale(s)
            * Matrix4x4.CreateFromQuaternion(r)
            * Matrix4x4.CreateTranslation(t);
    }

    private static Vector3? ParseVec3(float[]? v)
        => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : null;

    private static Quaternion? ParseQuat(float[]? q)
        => q is { Length: >= 4 } ? new Quaternion(q[0], q[1], q[2], q[3]) : null;
}
