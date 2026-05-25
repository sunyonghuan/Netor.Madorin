namespace DesktopPet.Models.Gltf;

public sealed class VrmProfile
{
    internal VrmProfile(
        string specVersion,
        IReadOnlyDictionary<string, int> humanoidBoneNodeIndices,
        IReadOnlyList<VrmExpressionPreset> expressionPresets,
        VrmLookAt? lookAt,
        VrmSpringBoneInfo springBone)
    {
        SpecVersion = specVersion;
        HumanoidBoneNodeIndices = humanoidBoneNodeIndices;
        ExpressionPresets = expressionPresets;
        LookAt = lookAt;
        SpringBone = springBone;
    }

    /// <summary>"0.x" for VRM 0, "1.0" for VRM 1.</summary>
    public string SpecVersion { get; }

    /// <summary>Maps VRM bone name (see <see cref="VrmHumanoidBone"/>) to glTF node index.</summary>
    public IReadOnlyDictionary<string, int> HumanoidBoneNodeIndices { get; }

    /// <summary>Expression presets parsed from VRM (reserved; not yet driven at runtime).</summary>
    public IReadOnlyList<VrmExpressionPreset> ExpressionPresets { get; }

    /// <summary>Look-at config (reserved; not yet driven at runtime).</summary>
    public VrmLookAt? LookAt { get; }

    /// <summary>Spring-bone summary (reserved; not yet driven at runtime).</summary>
    public VrmSpringBoneInfo SpringBone { get; }

    public int? GetBoneNodeIndex(string boneName)
        => HumanoidBoneNodeIndices.TryGetValue(boneName, out var idx) ? idx : null;
}

/// <summary>Morph-target bind: which morph target on which mesh to drive, and at what normalized weight.</summary>
public sealed record VrmMorphTargetBind(string MeshName, int MorphTargetIndex, float NormalizedWeight);

/// <summary>Preset expression with resolved morph-target binds.</summary>
public sealed record VrmExpressionPreset(
    string Name,
    bool IsBinary,
    IReadOnlyList<VrmMorphTargetBind> MorphTargetBinds);

/// <summary>Look-at configuration; reserved for future gaze tracking.</summary>
public sealed record VrmLookAt(string Type);

/// <summary>Spring-bone summary; reserved for future physics simulation.</summary>
public sealed record VrmSpringBoneInfo(int JointCount, int ColliderCount);
