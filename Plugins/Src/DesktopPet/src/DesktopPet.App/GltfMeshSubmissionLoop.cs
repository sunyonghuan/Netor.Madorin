using System.Numerics;
using DesktopPet.Models.Gltf;
using DesktopPet.Rendering.D3D11;

internal sealed class GltfMeshSubmissionLoop : IDisposable
{
    private readonly GltfModel _model;
    private readonly IRenderHost _renderHost;
    private readonly GltfAnimationEvaluator? _animator;
    private readonly VrmLookAtController? _lookAtController;
    private readonly VrmSpringBoneSimulator? _springBoneSimulator;
    private readonly CancellationTokenSource _stopped = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _morphLock = new();

    // Normalise transform: scales/translates the whole model so it fits the camera view
    private readonly Matrix4x4 _normalizeTransform;

    // Mutable: rebuilt when morphs change
    private D3D11MeshItem[] _baseItems;
    private int[] _nodeIndices;
    private D3D11MeshItem[] _staticItems;

    // Skin data: per-primitive skin index and per-skin joint/ibm info (immutable after ctor)
    private readonly int[] _skinIndices;          // parallel to _baseItems
    private readonly SkinData[] _skinDataCache;   // indexed by skin index

    private VrmExpressionEvaluator? _expressionEvaluator;
    private volatile bool _morphDirty;
    private volatile bool _lookAtActive;
    private volatile bool _springBoneEnabled;

    private DateTimeOffset _lastFrameTime = DateTimeOffset.UtcNow;
    private Thread? _thread;
    private bool _disposed;

    public GltfMeshSubmissionLoop(GltfModel model, IRenderHost renderHost)
    {
        _model       = model;
        _renderHost  = renderHost;
        // 智能选择待机动画：优先找名字含 idle/survey/stand/wait 等关键字的动画
        var idleIndex = model.PickIdleAnimationIndex();
        _animator    = idleIndex >= 0 ? model.CreateAnimationEvaluator(idleIndex) : null;
        _lookAtController     = model.CreateLookAtController();
        _springBoneSimulator  = model.CreateSpringBoneSimulator();
        (_baseItems, _nodeIndices) = GltfMeshItemMapper.ToBaseItems(model);
        _normalizeTransform = ComputeNormalizeTransform(model);

        // ── Pre-cache skin data (inverse-bind matrices + joint node indices) ──
        var primitives = model.ExtractMeshPrimitives();
        _skinIndices = new int[primitives.Count];
        for (var i = 0; i < primitives.Count; i++)
            _skinIndices[i] = primitives[i].SkinIndex;

        var skinCount = 0;
        foreach (var p in primitives)
            if (p.SkinIndex >= 0 && p.SkinIndex + 1 > skinCount)
                skinCount = p.SkinIndex + 1;

        // Compute bind-pose world matrices once (rest-pose, no animation overrides)
        var bindPoseWorldMatrices = model.ComputeNodeWorldMatrices();

        _skinDataCache = new SkinData[skinCount];
        for (var s = 0; s < skinCount; s++)
        {
            var joints = model.GetSkinJoints(s);
            // Compute IBM at runtime from the bind-pose world matrices.
            // IBM_j = inv(jointWorld_j_at_bind_pose)
            var ibms = ComputeInverseBindMatrices(joints, bindPoseWorldMatrices);
            _skinDataCache[s] = new SkinData(joints, ibms);
        }

        // Must be initialised AFTER _skinIndices and _skinDataCache are ready
        _staticItems = _animator is null ? ApplyWorldMatrices(model.ComputeNodeWorldMatrices()) : [];
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;

        _thread = new Thread(SubmitFrames)
        {
            IsBackground = true,
            Name = "DesktopPet GLB Mesh Submission"
        };
        _thread.Start();
    }

    // ── Expression API ───────────────────────────────────────────────────────

    /// <summary>Activates a VRM expression by name (weight 0-1, default 1).</summary>
    public void SetExpression(string name, float weight = 1.0f)
    {
        lock (_morphLock)
        {
            _expressionEvaluator ??= _model.CreateExpressionEvaluator();
            _expressionEvaluator?.SetExpression(name, weight);
        }
        _morphDirty = true;
    }

    /// <summary>Removes all active VRM expressions.</summary>
    public void ClearExpressions()
    {
        lock (_morphLock) { _expressionEvaluator?.ClearExpressions(); }
        _morphDirty = true;
    }

    // ── LookAt API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drives eye bones toward the given normalized screen position (0-1 each axis, origin top-left).
    /// </summary>
    public void SetLookAtScreenPosition(float normalizedX, float normalizedY)
    {
        _lookAtController?.SetScreenPosition(normalizedX, normalizedY);
        if (_lookAtController is not null) _lookAtActive = true;
    }

    public void ClearLookAt()
    {
        _lookAtController?.Clear();
        _lookAtActive = false;
    }

    // ── SpringBone API ───────────────────────────────────────────────────────

    /// <summary>Enables spring-bone physics (hair/clothing simulation).</summary>
    public void EnableSpringBone()  => _springBoneEnabled = _springBoneSimulator is not null;
    public void DisableSpringBone() => _springBoneEnabled = false;

    // ── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopped.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _renderHost.SubmitMeshItems([]);
        _stopped.Dispose();
    }

    // ── Internal loop ───────────────────────────────────────────────────────

    private void SubmitFrames()
    {
        while (!_stopped.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var dt  = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

            if (_morphDirty) { RebuildBaseItems(); _morphDirty = false; }

            var needsDynamic = _animator is not null || _lookAtActive || _springBoneEnabled;
            var items = needsDynamic ? BuildDynamicItems(dt) : _staticItems;

            _renderHost.SubmitMeshItems(items);
            Thread.Sleep(33);
        }
    }

    private void RebuildBaseItems()
    {
        D3D11MeshItem[] newBase;
        int[] newIndices;
        lock (_morphLock)
        {
            (newBase, newIndices) = GltfMeshItemMapper.ToBaseItems(_model, _expressionEvaluator);
        }
        _baseItems   = newBase;
        _nodeIndices = newIndices;
        if (_animator is null && !_lookAtActive && !_springBoneEnabled)
            _staticItems = ApplyWorldMatrices(_model.ComputeNodeWorldMatrices());
    }

    private D3D11MeshItem[] BuildDynamicItems(float dt)
    {
        Dictionary<int, GltfNodeOverride>? overrides = null;

        if (_animator is not null)
        {
            var elapsed = (float)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
            overrides = _animator.Evaluate(elapsed);
        }

        if (_lookAtActive && _lookAtController is not null)
        {
            overrides ??= [];
            _lookAtController.ApplyOverrides(overrides);
        }

        var worldMatrices = _model.ComputeNodeWorldMatrices(overrides);

        if (_springBoneEnabled && _springBoneSimulator is not null)
        {
            overrides ??= [];
            _springBoneSimulator.Update(dt, worldMatrices, overrides);
        }

        return ApplyWorldMatrices(worldMatrices);
    }

    private D3D11MeshItem[] ApplyWorldMatrices(Dictionary<int, Matrix4x4> worldMatrices)
    {
        var result = new D3D11MeshItem[_baseItems.Length];
        for (var i = 0; i < _baseItems.Length; i++)
        {
            Matrix4x4 world = Matrix4x4.Identity;
            worldMatrices.TryGetValue(_nodeIndices[i], out world);

            // Prepend the normalize transform so the model always fits the camera view
            var finalWorld = world * _normalizeTransform;

            // ── Linear Blend Skinning: compute joint matrices for this primitive ──
            Matrix4x4[]? jointMatrices = null;
            var skinIdx = _skinIndices.Length > i ? _skinIndices[i] : -1;
            if (skinIdx >= 0 && skinIdx < _skinDataCache.Length)
            {
                var skin = _skinDataCache[skinIdx];
                if (skin.JointNodeIndices.Count > 0)
                {
                    jointMatrices = ComputeJointMatrices(skin, worldMatrices, _normalizeTransform);
                }
            }

            result[i] = _baseItems[i] with
            {
                WorldTransform = finalWorld,
                JointMatrices  = jointMatrices
            };
        }
        return result;
    }

    /// <summary>
    /// Computes per-joint skin matrices for GPU LBS.
    ///
    /// Convention: System.Numerics uses row-vector math (v' = v * M).
    /// GltfNodeTransformTree builds world matrices with  world = local * parentWorld.
    ///
    /// The glTF spec defines skin matrix (column-vector convention):
    ///   skinMatrix_col[j] = jointGlobalTransform_col[j] × inverseBindMatrix_col[j]
    ///
    /// Converting to row-vector (.NET) — transposing reverses multiplication order:
    ///   skinMatrix_row[j] = ibm_row[j] × jointGlobalTransform_row[j]
    ///
    /// GltfNodeTransformTree produces row-vector world matrices, so:
    ///   finalJointMatrix[j] = ibm[j] × jointWorld[j] × normalizeTransform
    /// </summary>
    private static Matrix4x4[] ComputeInverseBindMatrices(
        IReadOnlyList<int> jointNodeIndices,
        Dictionary<int, Matrix4x4> bindPoseWorldMatrices)
    {
        var count = jointNodeIndices.Count;
        var result = new Matrix4x4[count];
        for (var j = 0; j < count; j++)
        {
            bindPoseWorldMatrices.TryGetValue(jointNodeIndices[j], out var jw);
            if (jw == default) jw = Matrix4x4.Identity;
            if (!Matrix4x4.Invert(jw, out var ibm))
                ibm = Matrix4x4.Identity;
            result[j] = ibm;
        }
        return result;
    }

    private static Matrix4x4[] ComputeJointMatrices(
        SkinData skin,
        Dictionary<int, Matrix4x4> worldMatrices,
        Matrix4x4 normalizeTransform)
    {
        var count = skin.JointNodeIndices.Count;
        var result = new Matrix4x4[count];
        for (var j = 0; j < count; j++)
        {
            var jointNodeIdx = skin.JointNodeIndices[j];
            if (!worldMatrices.TryGetValue(jointNodeIdx, out var jointWorld))
                jointWorld = Matrix4x4.Identity;   // fallback: identity, not zero
            var ibm = j < skin.InverseBindMatrices.Length
                ? skin.InverseBindMatrices[j]
                : Matrix4x4.Identity;

            // Row-vector convention: skinMatrix = ibm × jointWorld × normalizeTransform
            // (glTF col-vector: jointWorld × ibm; transposing reverses order)
            result[j] = ibm * jointWorld * normalizeTransform;
        }
        return result;
    }

    /// <summary>
    /// Computes a world-space normalization matrix that scales and centers the model so its
    /// AABB fits within a ~1.8-unit sphere centered at the origin, matching the default camera
    /// (position Z=2.8, looking at origin, FOV=45°).
    /// </summary>
    private static Matrix4x4 ComputeNormalizeTransform(GltfModel model)
    {
        // Use rest-pose world matrices
        var worldMatrices = model.ComputeNodeWorldMatrices();
        var primitives    = model.ExtractMeshPrimitives();

        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;
        var minZ = float.MaxValue; var maxZ = float.MinValue;

        foreach (var prim in primitives)
        {
            worldMatrices.TryGetValue(prim.NodeIndex, out var nodeWorld);
            var positions = prim.Positions;
            var count = positions.Length / 3;
            for (var i = 0; i < count; i++)
            {
                var local = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                var world = Vector3.Transform(local, nodeWorld);
                if (world.X < minX) minX = world.X;
                if (world.X > maxX) maxX = world.X;
                if (world.Y < minY) minY = world.Y;
                if (world.Y > maxY) maxY = world.Y;
                if (world.Z < minZ) minZ = world.Z;
                if (world.Z > maxZ) maxZ = world.Z;
            }
        }

        // If no geometry was found, return identity
        if (minX > maxX || minY > maxY || minZ > maxZ)
        {
            return Matrix4x4.Identity;
        }

        var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        var extentX = maxX - minX;
        var extentY = maxY - minY;
        var extentZ = maxZ - minZ;
        var maxExtent = MathF.Max(extentX, MathF.Max(extentY, extentZ));

        if (maxExtent < 1e-6f)
        {
            return Matrix4x4.Identity;
        }

        // Target size: fit into ~1.8 units (comfortable for camera at distance 2.8, FOV 45°)
        const float targetSize = 1.8f;
        var scale = targetSize / maxExtent;

        // Scale first, then translate the scaled center to the origin.
        var scaledCenter = center * scale;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(-scaledCenter);
    }

    // ── SkinData helper ──────────────────────────────────────────────────────

    private sealed class SkinData(IReadOnlyList<int> jointNodeIndices, Matrix4x4[] inverseBindMatrices)
    {
        public IReadOnlyList<int> JointNodeIndices   { get; } = jointNodeIndices;
        public Matrix4x4[]       InverseBindMatrices { get; } = inverseBindMatrices;
    }
}
