namespace DesktopPet.Models.Gltf;

/// <summary>
/// Applies VRM expression weights to mesh morph targets.
/// Thread-safety: callers must synchronize Set/Clear vs BlendPositions.
/// </summary>
public sealed class VrmExpressionEvaluator
{
    private readonly VrmProfile _profile;
    private readonly Dictionary<string, float> _active = new(StringComparer.OrdinalIgnoreCase);

    internal VrmExpressionEvaluator(VrmProfile profile) => _profile = profile;

    /// <summary>Monotonically increases each time expressions change; used to invalidate GPU buffer cache keys.</summary>
    public int Version { get; private set; }

    public void SetExpression(string name, float weight = 1.0f)
    {
        _active[name] = weight;
        Version++;
    }

    public void ClearExpressions()
    {
        if (_active.Count == 0) return;
        _active.Clear();
        Version++;
    }

    public bool HasActiveMorphs(string meshName)
    {
        foreach (var (exprName, _) in _active)
        {
            var preset = FindPreset(exprName);
            if (preset is null) continue;
            foreach (var bind in preset.MorphTargetBinds)
                if (string.Equals(bind.MeshName, meshName, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Returns blended positions for a mesh primitive, or <c>null</c> if no active morphs apply.
    /// </summary>
    public float[]? BlendPositions(string meshName, float[] basePositions, IReadOnlyList<float[]>? morphDeltas)
    {
        if (morphDeltas is null || morphDeltas.Count == 0 || _active.Count == 0)
            return null;

        float[]? result = null;

        foreach (var (exprName, exprWeight) in _active)
        {
            var preset = FindPreset(exprName);
            if (preset is null) continue;

            foreach (var bind in preset.MorphTargetBinds)
            {
                if (!string.Equals(bind.MeshName, meshName, StringComparison.OrdinalIgnoreCase)) continue;
                if (bind.MorphTargetIndex >= morphDeltas.Count) continue;

                var deltas = morphDeltas[bind.MorphTargetIndex];
                if (deltas.Length != basePositions.Length) continue;

                if (result is null)
                    result = (float[])basePositions.Clone();

                var w = bind.NormalizedWeight * exprWeight;
                for (var i = 0; i < result.Length; i++)
                    result[i] += deltas[i] * w;
            }
        }

        return result;
    }

    private VrmExpressionPreset? FindPreset(string name)
    {
        foreach (var p in _profile.ExpressionPresets)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
