using System.Text.Json.Serialization;
using System.Text.Json;

namespace DesktopPet.Models.Gltf;

internal sealed record GltfManifest(
    GltfAsset? Asset,
    IReadOnlyList<GltfScene>? Scenes,
    IReadOnlyList<GltfNode>? Nodes,
    IReadOnlyList<GltfMesh>? Meshes,
    IReadOnlyList<GltfMaterial>? Materials,
    IReadOnlyList<GltfTexture>? Textures,
    IReadOnlyList<GltfImage>? Images,
    IReadOnlyList<GltfAnimation>? Animations,
    IReadOnlyList<GltfBuffer>? Buffers,
    IReadOnlyList<GltfBufferView>? BufferViews,
    IReadOnlyList<GltfAccessor>? Accessors,
    IReadOnlyList<string>? ExtensionsUsed,
    IReadOnlyList<string>? ExtensionsRequired,
    IReadOnlyDictionary<string, JsonElement>? Extensions,
    int? Scene,
    IReadOnlyList<GltfSkin>? Skins = null);

internal sealed record GltfAsset(string? Version, string? Generator);

internal sealed record GltfScene(IReadOnlyList<int>? Nodes, string? Name);

internal sealed record GltfNode(
    string? Name,
    int? Mesh,
    IReadOnlyList<int>? Children,
    float[]? Translation,
    float[]? Rotation,
    float[]? Scale,
    float[]? Matrix,
    int? Skin = null);

internal sealed record GltfMesh(string? Name, IReadOnlyList<GltfPrimitive>? Primitives);

internal sealed record GltfBuffer(string? Uri, int ByteLength);

internal sealed record GltfPrimitive(
    Dictionary<string, int>? Attributes,
    int? Indices,
    int? Material,
    int? Mode,
    IReadOnlyList<Dictionary<string, int>>? Targets);

internal sealed record GltfMaterial(string? Name, GltfPbrMetallicRoughness? PbrMetallicRoughness, string? AlphaMode);

internal sealed record GltfPbrMetallicRoughness(float[]? BaseColorFactor, GltfTextureRef? BaseColorTexture);

internal sealed record GltfTextureRef(int Index, int? TexCoord);

internal sealed record GltfTexture(int? Source);

internal sealed record GltfImage(string? Uri, int? BufferView, string? MimeType);

internal sealed record GltfAnimation(
    string? Name,
    IReadOnlyList<GltfAnimationChannel>? Channels,
    IReadOnlyList<GltfAnimationSampler>? Samplers);

internal sealed record GltfAnimationChannel(int Sampler, GltfAnimationTarget? Target);

internal sealed record GltfAnimationTarget(int? Node, string? Path);

internal sealed record GltfAnimationSampler(int Input, string? Interpolation, int Output);

internal sealed record GltfBufferView(int? Buffer, int? ByteOffset, int ByteLength, int? ByteStride);

internal sealed record GltfAccessor(int? BufferView, int? ByteOffset, int ComponentType, int Count, string? Type);

/// <summary>glTF skin: joints list + optional inverseBindMatrices accessor index.</summary>
internal sealed record GltfSkin(
    string? Name,
    IReadOnlyList<int>? Joints,
    int? InverseBindMatrices,
    int? Skeleton);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GltfManifest))]
[JsonSerializable(typeof(GltfAsset))]
[JsonSerializable(typeof(GltfScene))]
[JsonSerializable(typeof(GltfNode))]
[JsonSerializable(typeof(GltfMesh))]
[JsonSerializable(typeof(GltfBuffer))]
[JsonSerializable(typeof(GltfPrimitive))]
[JsonSerializable(typeof(GltfMaterial))]
[JsonSerializable(typeof(GltfPbrMetallicRoughness))]
[JsonSerializable(typeof(GltfTextureRef))]
[JsonSerializable(typeof(GltfTexture))]
[JsonSerializable(typeof(GltfImage))]
[JsonSerializable(typeof(GltfAnimation))]
[JsonSerializable(typeof(GltfAnimationChannel))]
[JsonSerializable(typeof(GltfAnimationTarget))]
[JsonSerializable(typeof(GltfAnimationSampler))]
[JsonSerializable(typeof(GltfBufferView))]
[JsonSerializable(typeof(GltfAccessor))]
[JsonSerializable(typeof(GltfSkin))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<Dictionary<string, int>>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
internal sealed partial class GltfJsonContext : JsonSerializerContext
{
}
