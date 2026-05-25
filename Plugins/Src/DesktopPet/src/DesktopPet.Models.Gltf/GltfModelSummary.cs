namespace DesktopPet.Models.Gltf;

public sealed record GltfModelSummary(
    string Name,
    string SourcePath,
    int SceneCount,
    int NodeCount,
    int MeshCount,
    int MaterialCount,
    int TextureCount,
    int ImageCount,
    int AnimationCount,
    int BufferViewCount,
    int AccessorCount,
    bool HasBinaryChunk,
    bool HasVrmExtension,
    IReadOnlyList<string> UsedExtensions,
    IReadOnlyList<string> RequiredExtensions);
