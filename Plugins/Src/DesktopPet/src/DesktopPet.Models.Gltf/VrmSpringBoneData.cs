using System.Numerics;
using System.Text.Json;

namespace DesktopPet.Models.Gltf;

public sealed record VrmSpringJoint(
    int NodeIndex,
    float HitRadius,
    float Stiffness,
    float GravityPower,
    Vector3 GravityDir,
    float DragForce);

public sealed record VrmSpringChain(IReadOnlyList<VrmSpringJoint> Joints);

public sealed record VrmSpringCollider(int NodeIndex, Vector3 LocalOffset, float Radius);

/// <summary>Full spring-bone data parsed from a VRM model.</summary>
public sealed class VrmSpringBoneData
{
    private VrmSpringBoneData() { }

    public IReadOnlyList<VrmSpringChain> Chains   { get; private init; } = [];
    public IReadOnlyList<VrmSpringCollider> Colliders { get; private init; } = [];
    public bool HasData => Chains.Count > 0;

    internal static VrmSpringBoneData? TryParse(GltfManifest manifest)
    {
        var exts = manifest.Extensions;
        if (exts is null) return null;

        if (exts.TryGetValue("VRMC_springBone", out var sb1))
            return ParseVrm1(sb1, manifest);

        if (exts.TryGetValue("VRM", out var vrm0)
            && vrm0.TryGetProperty("secondaryAnimation", out var sa))
            return ParseVrm0(sa, manifest);

        return null;
    }

    // ── VRM 1.0 ─────────────────────────────────────────────────────────────

    private static VrmSpringBoneData ParseVrm1(JsonElement sb, GltfManifest manifest)
    {
        var colliders = ParseColliders1(sb);
        var chains    = new List<VrmSpringChain>();

        if (sb.TryGetProperty("springs", out var springs) && springs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spring in springs.EnumerateArray())
            {
                if (!spring.TryGetProperty("joints", out var jointsEl)
                    || jointsEl.ValueKind != JsonValueKind.Array) continue;

                var joints = new List<VrmSpringJoint>();
                foreach (var j in jointsEl.EnumerateArray())
                {
                    if (!j.TryGetProperty("node", out var nEl) || !nEl.TryGetInt32(out var nIdx)) continue;
                    joints.Add(new VrmSpringJoint(
                        NodeIndex:    nIdx,
                        HitRadius:    GetFloat(j, "hitRadius",    0.02f),
                        Stiffness:    GetFloat(j, "stiffness",    1.0f),
                        GravityPower: GetFloat(j, "gravityPower", 0f),
                        GravityDir:   GetVec3Array(j, "gravityDir") ?? new Vector3(0, -1, 0),
                        DragForce:    GetFloat(j, "dragForce",    0.4f)));
                }
                if (joints.Count >= 2) chains.Add(new VrmSpringChain(joints));
            }
        }

        return new VrmSpringBoneData { Chains = chains, Colliders = colliders };
    }

    private static List<VrmSpringCollider> ParseColliders1(JsonElement sb)
    {
        var result = new List<VrmSpringCollider>();
        if (!sb.TryGetProperty("colliders", out var cols) || cols.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var col in cols.EnumerateArray())
        {
            if (!col.TryGetProperty("node", out var nEl) || !nEl.TryGetInt32(out var nIdx)) continue;
            if (!col.TryGetProperty("shape", out var shape)
                || !shape.TryGetProperty("sphere", out var sphere)) continue;

            result.Add(new VrmSpringCollider(
                nIdx,
                GetVec3Array(sphere, "offset") ?? Vector3.Zero,
                GetFloat(sphere, "radius", 0.1f)));
        }
        return result;
    }

    // ── VRM 0.x ─────────────────────────────────────────────────────────────

    private static VrmSpringBoneData ParseVrm0(JsonElement sa, GltfManifest manifest)
    {
        var colliders = ParseColliders0(sa);
        var chains    = new List<VrmSpringChain>();

        if (sa.TryGetProperty("boneGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("bones", out var bonesEl)
                    || bonesEl.ValueKind != JsonValueKind.Array) continue;

                // VRM 0.x uses "stiffiness" (typo in spec) or "stiffness"
                var stiffness    = group.TryGetProperty("stiffiness", out var st1) && st1.TryGetSingle(out var sv1) ? sv1
                                 : GetFloat(group, "stiffness", 1.0f);
                var drag         = GetFloat(group, "dragForce",    0.4f);
                var hitRadius    = GetFloat(group, "hitRadius",    0.02f);
                var gravPower    = GetFloat(group, "gravityPower", 0f);
                var gravDir      = GetVec3Object(group, "gravityDir") ?? new Vector3(0, -1, 0);

                foreach (var boneEl in bonesEl.EnumerateArray())
                {
                    if (!boneEl.TryGetInt32(out var rootIdx)) continue;
                    var chainNodes = WalkChain(manifest, rootIdx);
                    if (chainNodes.Count < 2) continue;

                    var joints = chainNodes
                        .Select(n => new VrmSpringJoint(n, hitRadius, stiffness, gravPower, gravDir, drag))
                        .ToList();
                    chains.Add(new VrmSpringChain(joints));
                }
            }
        }

        return new VrmSpringBoneData { Chains = chains, Colliders = colliders };
    }

    private static List<VrmSpringCollider> ParseColliders0(JsonElement sa)
    {
        var result = new List<VrmSpringCollider>();
        if (!sa.TryGetProperty("colliderGroups", out var groups)
            || groups.ValueKind != JsonValueKind.Array) return result;

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("node", out var nEl) || !nEl.TryGetInt32(out var nIdx)) continue;
            if (!group.TryGetProperty("colliders", out var cols) || cols.ValueKind != JsonValueKind.Array) continue;

            foreach (var col in cols.EnumerateArray())
            {
                result.Add(new VrmSpringCollider(
                    nIdx,
                    GetVec3Object(col, "offset") ?? Vector3.Zero,
                    GetFloat(col, "radius", 0.1f)));
            }
        }
        return result;
    }

    // Follow first child recursively to build a linear chain from a root node
    private static List<int> WalkChain(GltfManifest manifest, int rootIdx)
    {
        var chain = new List<int>();
        var nodes = manifest.Nodes;
        if (nodes is null) return chain;
        var cur = rootIdx;
        while (cur >= 0 && cur < nodes.Count)
        {
            chain.Add(cur);
            var ch = nodes[cur].Children;
            if (ch is null || ch.Count == 0) break;
            cur = ch[0];
        }
        return chain;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static float GetFloat(JsonElement el, string name, float fallback)
        => el.TryGetProperty(name, out var p) && p.TryGetSingle(out var v) ? v : fallback;

    private static Vector3? GetVec3Array(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var arr = p.EnumerateArray().Select(e => e.TryGetSingle(out var v) ? v : 0f).ToArray();
        return arr.Length >= 3 ? new Vector3(arr[0], arr[1], arr[2]) : null;
    }

    private static Vector3? GetVec3Object(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var x = GetFloat(p, "x", 0f); var y = GetFloat(p, "y", 0f); var z = GetFloat(p, "z", 0f);
        return new Vector3(x, y, z);
    }
}
