using System.Text.Json.Serialization;

namespace DesktopPet.Models.Live2D;

public sealed record Live2DModelManifest(
    int Version,
    Live2DFileReferences FileReferences,
    IReadOnlyList<Live2DParameterGroup>? Groups);

public sealed record Live2DFileReferences(
    string Moc,
    IReadOnlyList<string> Textures,
    string? Pose,
    IReadOnlyDictionary<string, IReadOnlyList<Live2DMotionReference>>? Motions);

public sealed record Live2DMotionReference(
    string File,
    float? FadeInTime,
    float? FadeOutTime);

public sealed record Live2DParameterGroup(
    string Target,
    string Name,
    IReadOnlyList<string> Ids);

public sealed record Live2DPoseManifest(
    string Type,
    float? FadeInTime,
    IReadOnlyList<IReadOnlyList<Live2DPosePart>> Groups);

public sealed record Live2DPosePart(
    string Id,
    IReadOnlyList<string>? Link);

public sealed record Live2DMotionManifest(
    Live2DMotionMeta Meta,
    IReadOnlyList<Live2DMotionCurve> Curves);

public sealed record Live2DMotionMeta(
    float Duration,
    float Fps,
    bool Loop);

public sealed record Live2DMotionCurve(
    string Target,
    string Id,
    IReadOnlyList<float> Segments);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Live2DModelManifest))]
[JsonSerializable(typeof(Live2DFileReferences))]
[JsonSerializable(typeof(Live2DMotionReference))]
[JsonSerializable(typeof(Live2DParameterGroup))]
[JsonSerializable(typeof(Live2DPoseManifest))]
[JsonSerializable(typeof(Live2DPosePart))]
[JsonSerializable(typeof(Live2DMotionManifest))]
[JsonSerializable(typeof(Live2DMotionMeta))]
[JsonSerializable(typeof(Live2DMotionCurve))]
internal sealed partial class Live2DJsonContext : JsonSerializerContext
{
}
