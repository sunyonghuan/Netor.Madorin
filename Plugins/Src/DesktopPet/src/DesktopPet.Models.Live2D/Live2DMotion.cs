namespace DesktopPet.Models.Live2D;

public sealed record Live2DMotion(
    float Duration,
    bool Loop,
    IReadOnlyList<Live2DMotionTrack> Tracks);

public sealed record Live2DMotionTrack(
    Live2DMotionTarget Target,
    string Id,
    IReadOnlyList<Live2DMotionKeyFrame> KeyFrames);

public sealed record Live2DMotionKeyFrame(
    float Time,
    float Value);

public enum Live2DMotionTarget
{
    Parameter,
    PartOpacity
}
