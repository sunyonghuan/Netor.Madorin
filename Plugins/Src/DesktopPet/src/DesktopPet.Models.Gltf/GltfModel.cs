using System.Numerics;

namespace DesktopPet.Models.Gltf;

public sealed class GltfModel
{
    private readonly GltfManifest _manifest;

    internal GltfModel(GltfManifest manifest, GltfModelSummary summary, string json, byte[] binaryChunk)
    {
        _manifest = manifest;
        Summary = summary;
        Json = json;
        BinaryChunk = binaryChunk;
    }

    public GltfModelSummary Summary { get; }

    public string Json { get; }

    public byte[] BinaryChunk { get; }

    public IReadOnlyList<GltfMeshPrimitive> ExtractMeshPrimitives()
        => GltfMeshExtractor.Extract(_manifest, BinaryChunk);

    public byte[]? ExtractImageBytes(int imageIndex)
        => GltfImageExtractor.ExtractImageBytes(
            _manifest,
            BinaryChunk,
            imageIndex,
            Path.GetDirectoryName(Summary.SourcePath) ?? string.Empty);

    public (float R, float G, float B, float A) GetBaseColorFactor(int? materialIndex)
    {
        if (materialIndex is null)
        {
            return (1f, 1f, 1f, 1f);
        }

        var materials = _manifest.Materials;
        if (materials is null || materialIndex.Value < 0 || materialIndex.Value >= materials.Count)
        {
            return (1f, 1f, 1f, 1f);
        }

        var factor = materials[materialIndex.Value].PbrMetallicRoughness?.BaseColorFactor;
        return factor is { Length: >= 4 }
            ? (factor[0], factor[1], factor[2], factor[3])
            : (1f, 1f, 1f, 1f);
    }

    public int? GetBaseColorImageIndex(int? materialIndex)
    {
        if (materialIndex is null)
        {
            return null;
        }

        var materials = _manifest.Materials;
        if (materials is null || materialIndex.Value < 0 || materialIndex.Value >= materials.Count)
        {
            return null;
        }

        var textureRef = materials[materialIndex.Value].PbrMetallicRoughness?.BaseColorTexture;
        if (textureRef is null)
        {
            return null;
        }

        var textures = _manifest.Textures;
        if (textures is null || textureRef.Index < 0 || textureRef.Index >= textures.Count)
        {
            return null;
        }

        return textures[textureRef.Index].Source;
    }

    public int AnimationCount => _manifest.Animations?.Count ?? 0;

    public GltfAnimationEvaluator? CreateAnimationEvaluator(int animationIndex)
        => GltfAnimationEvaluator.Build(_manifest, BinaryChunk, animationIndex);

    public Dictionary<int, Matrix4x4> ComputeNodeWorldMatrices(
        IReadOnlyDictionary<int, GltfNodeOverride>? overrides = null)
        => GltfNodeTransformTree.ComputeWorldMatrices(_manifest, overrides);

    public VrmProfile? TryGetVrmProfile()
        => VrmProfileParser.TryParse(_manifest);

    public VrmExpressionEvaluator? CreateExpressionEvaluator()
    {
        var profile = TryGetVrmProfile();
        return profile is null ? null : new VrmExpressionEvaluator(profile);
    }

    public VrmLookAtController? CreateLookAtController()
    {
        var profile = TryGetVrmProfile();
        if (profile is null) return null;
        var ctrl = new VrmLookAtController(profile);
        return ctrl.HasEyeBones ? ctrl : null;
    }

    public VrmSpringBoneSimulator? CreateSpringBoneSimulator()
    {
        var data = VrmSpringBoneData.TryParse(_manifest);
        return data is { HasData: true } ? new VrmSpringBoneSimulator(data, _manifest) : null;
    }
}
