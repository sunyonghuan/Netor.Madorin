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

    // Mutable: rebuilt when morphs change
    private D3D11MeshItem[] _baseItems;
    private int[] _nodeIndices;
    private D3D11MeshItem[] _staticItems;

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
        _animator    = model.AnimationCount > 0 ? model.CreateAnimationEvaluator(0) : null;
        _lookAtController     = model.CreateLookAtController();
        _springBoneSimulator  = model.CreateSpringBoneSimulator();
        (_baseItems, _nodeIndices) = GltfMeshItemMapper.ToBaseItems(model);
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
            result[i] = _baseItems[i] with { WorldTransform = world };
        }
        return result;
    }
}
