using System.Numerics;

namespace DesktopPet.Models.Gltf;

/// <summary>
/// Verlet spring-bone physics for VRM hair/clothing simulation.
/// Call <see cref="Update"/> each frame; it mutates <paramref name="workingWorldMatrices"/> in place
/// and injects rotation overrides into <paramref name="overrides"/>.
/// </summary>
public sealed class VrmSpringBoneSimulator
{
    private readonly VrmSpringBoneData _data;
    private readonly GltfManifest _manifest;
    private readonly IReadOnlyDictionary<int, int> _nodeParents;

    // [chainIdx][jointIdx] = simulated world position of that joint's "bone endpoint"
    // (= world pos of next joint in chain, or virtual tail for the last joint)
    private readonly Vector3[][] _simPos;
    private readonly Vector3[][] _prevPos;
    private readonly float[][]   _boneLengths;
    private readonly Vector3[][] _localRestDirs; // from joint toward its endpoint, in joint's local space

    private bool _initialized;

    internal VrmSpringBoneSimulator(VrmSpringBoneData data, GltfManifest manifest)
    {
        _data        = data;
        _manifest    = manifest;
        _nodeParents = BuildParentMap(manifest);

        var n = data.Chains.Count;
        _simPos        = new Vector3[n][];
        _prevPos       = new Vector3[n][];
        _boneLengths   = new float[n][];
        _localRestDirs = new Vector3[n][];

        for (var ci = 0; ci < n; ci++)
        {
            var c = data.Chains[ci].Joints.Count;
            _simPos[ci]        = new Vector3[c];
            _prevPos[ci]       = new Vector3[c];
            _boneLengths[ci]   = new float[c];
            _localRestDirs[ci] = new Vector3[c];
        }
    }

    // ── Initialization (called on first Update) ──────────────────────────────

    private void Initialize(IReadOnlyDictionary<int, Matrix4x4> wm)
    {
        for (var ci = 0; ci < _data.Chains.Count; ci++)
        {
            var joints = _data.Chains[ci].Joints;
            for (var ji = 0; ji < joints.Count; ji++)
            {
                var nodeIdx  = joints[ji].NodeIndex;
                var nodeMat  = wm.TryGetValue(nodeIdx, out var m) ? m : Matrix4x4.Identity;
                var nodePos  = nodeMat.Translation;

                Vector3 endpointPos;
                if (ji < joints.Count - 1)
                {
                    var nextIdx = joints[ji + 1].NodeIndex;
                    endpointPos = wm.TryGetValue(nextIdx, out var nm) ? nm.Translation : nodePos;
                }
                else
                {
                    // Virtual tail: continue direction from previous joint
                    Vector3 dir;
                    if (ji > 0)
                    {
                        var prevIdx = joints[ji - 1].NodeIndex;
                        var prevPos = wm.TryGetValue(prevIdx, out var pm) ? pm.Translation : nodePos;
                        var d = nodePos - prevPos;
                        dir = d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : -Vector3.UnitY;
                    }
                    else dir = -Vector3.UnitY;
                    endpointPos = nodePos + dir * joints[ji].HitRadius;
                }

                _simPos[ci][ji]  = endpointPos;
                _prevPos[ci][ji] = endpointPos;
                _boneLengths[ci][ji] = MathF.Max(Vector3.Distance(nodePos, endpointPos), 1e-4f);

                var toEndWorld = endpointPos - nodePos;
                _localRestDirs[ci][ji] = Matrix4x4.Invert(nodeMat, out var inv)
                    ? Vector3.Normalize(Vector3.TransformNormal(toEndWorld, inv))
                    : -Vector3.UnitY;
            }
        }
        _initialized = true;
    }

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update(
        float dt,
        Dictionary<int, Matrix4x4> workingWorldMatrices,
        Dictionary<int, GltfNodeOverride> overrides)
    {
        if (!_initialized) { Initialize(workingWorldMatrices); return; }

        dt = Math.Clamp(dt, 1f / 120f, 1f / 10f); // clamp to sane range

        // Precompute collider world centres
        var colPos = new Vector3[_data.Colliders.Count];
        for (var k = 0; k < _data.Colliders.Count; k++)
        {
            var col = _data.Colliders[k];
            colPos[k] = workingWorldMatrices.TryGetValue(col.NodeIndex, out var cm)
                ? Vector3.Transform(col.LocalOffset, cm)
                : col.LocalOffset;
        }

        for (var ci = 0; ci < _data.Chains.Count; ci++)
        {
            var joints = _data.Chains[ci].Joints;
            for (var ji = 0; ji < joints.Count; ji++)
            {
                var joint   = joints[ji];
                var parentMat = workingWorldMatrices.TryGetValue(joint.NodeIndex, out var pm)
                    ? pm : Matrix4x4.Identity;
                var parentPos = parentMat.Translation;
                if (!Matrix4x4.Decompose(parentMat, out _, out var parentRot, out _))
                    parentRot = Quaternion.Identity;

                // Rest direction in current world space
                var restDir    = Vector3.Normalize(Vector3.Transform(_localRestDirs[ci][ji], parentRot));
                var restTarget = parentPos + restDir * _boneLengths[ci][ji];

                var cur  = _simPos[ci][ji];
                var prev = _prevPos[ci][ji];

                // Verlet: inertia + drag
                var next = cur + (cur - prev) * (1f - joint.DragForce);

                // Gravity
                next += joint.GravityDir * (joint.GravityPower * dt * dt);

                // Spring: pull toward rest
                if (joint.Stiffness > 0f)
                    next = Vector3.Lerp(next, restTarget, Math.Clamp(joint.Stiffness * dt, 0f, 1f));

                // Bone-length constraint
                next = ConstrainLength(next, parentPos, _boneLengths[ci][ji], restTarget);

                // Sphere-collider avoidance
                for (var k = 0; k < _data.Colliders.Count; k++)
                {
                    var minD = joint.HitRadius + _data.Colliders[k].Radius;
                    var diff = next - colPos[k];
                    if (diff.LengthSquared() < minD * minD)
                    {
                        var push = diff.LengthSquared() > 1e-10f ? Vector3.Normalize(diff) : Vector3.UnitY;
                        next = colPos[k] + push * minD;
                        next = ConstrainLength(next, parentPos, _boneLengths[ci][ji], restTarget);
                    }
                }

                _prevPos[ci][ji] = cur;
                _simPos[ci][ji]  = next;

                // Rotation override: rotate parentNode to point toward next
                var toDir = (next - parentPos).LengthSquared() > 1e-10f
                    ? Vector3.Normalize(next - parentPos) : restDir;

                if (Vector3.Dot(restDir, toDir) < 0.9999f)
                    ApplyRotationOverride(joint.NodeIndex, restDir, toDir, parentRot, workingWorldMatrices, overrides);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ApplyRotationOverride(
        int nodeIdx,
        Vector3 fromDir, Vector3 toDir,
        Quaternion parentWorldRot,
        Dictionary<int, Matrix4x4> wm,
        Dictionary<int, GltfNodeOverride> overrides)
    {
        var rotDelta    = QuaternionBetween(fromDir, toDir);
        var newWorldRot = Quaternion.Normalize(rotDelta * parentWorldRot);

        // newLocalRot = Inverse(grandparentWorldRot) * newWorldRot
        var gpRot      = GetGrandparentWorldRot(nodeIdx, wm);
        var newLocalRot = Quaternion.Normalize(Quaternion.Inverse(gpRot) * newWorldRot);

        overrides[nodeIdx] = new GltfNodeOverride { Rotation = newLocalRot };
        UpdateWorldMatrix(nodeIdx, newLocalRot, wm);
    }

    private Quaternion GetGrandparentWorldRot(int nodeIdx, Dictionary<int, Matrix4x4> wm)
    {
        if (!_nodeParents.TryGetValue(nodeIdx, out var parentIdx)
            || !wm.TryGetValue(parentIdx, out var pm)) return Quaternion.Identity;
        return Matrix4x4.Decompose(pm, out _, out var r, out _) ? r : Quaternion.Identity;
    }

    private void UpdateWorldMatrix(int nodeIdx, Quaternion newLocalRot, Dictionary<int, Matrix4x4> wm)
    {
        var nodes = _manifest.Nodes;
        if (nodes is null || nodeIdx < 0 || nodeIdx >= nodes.Count) return;

        var node = nodes[nodeIdx];
        var t = node.Translation is { Length: >= 3 } tr ? new Vector3(tr[0], tr[1], tr[2]) : Vector3.Zero;
        var s = node.Scale      is { Length: >= 3 } sc ? new Vector3(sc[0], sc[1], sc[2]) : Vector3.One;

        var local = Matrix4x4.CreateScale(s)
                  * Matrix4x4.CreateFromQuaternion(newLocalRot)
                  * Matrix4x4.CreateTranslation(t);

        var parentMat = _nodeParents.TryGetValue(nodeIdx, out var pIdx) && wm.TryGetValue(pIdx, out var pm)
            ? pm : Matrix4x4.Identity;

        wm[nodeIdx] = local * parentMat;
    }

    private static Vector3 ConstrainLength(Vector3 pos, Vector3 origin, float length, Vector3 fallback)
    {
        var d = pos - origin;
        return d.LengthSquared() > 1e-10f
            ? origin + Vector3.Normalize(d) * length
            : fallback;
    }

    private static Quaternion QuaternionBetween(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.9999f)  return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            var perp = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var axis = Vector3.Normalize(Vector3.Cross(from, perp));
            return new Quaternion(axis, 0f); // 180° rotation
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + dot));
    }

    private static IReadOnlyDictionary<int, int> BuildParentMap(GltfManifest manifest)
    {
        var result = new Dictionary<int, int>();
        var nodes = manifest.Nodes;
        if (nodes is null) return result;
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i].Children is not null)
                foreach (var child in nodes[i].Children!)
                    result[child] = i;
        return result;
    }
}
