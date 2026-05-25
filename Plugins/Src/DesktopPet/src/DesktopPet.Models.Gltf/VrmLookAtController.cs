using System.Numerics;

namespace DesktopPet.Models.Gltf;

/// <summary>
/// Drives eye-bone rotations toward a screen-space look-at target.
/// Thread note: yaw/pitch are plain floats; a torn read produces at most one
/// visually incorrect frame, which is acceptable for animation.
/// </summary>
public sealed class VrmLookAtController
{
    private const float MaxYaw   = MathF.PI / 6f;   // 30°
    private const float MaxPitch = MathF.PI / 9f;   // 20°

    private readonly int? _leftEyeNodeIndex;
    private readonly int? _rightEyeNodeIndex;

    private float _yaw;
    private float _pitch;

    internal VrmLookAtController(VrmProfile profile)
    {
        _leftEyeNodeIndex  = profile.GetBoneNodeIndex(VrmHumanoidBone.LeftEye);
        _rightEyeNodeIndex = profile.GetBoneNodeIndex(VrmHumanoidBone.RightEye);
    }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Returns true if any eye bone is mapped (i.e., this controller can produce a visible effect).
    /// </summary>
    public bool HasEyeBones => _leftEyeNodeIndex.HasValue || _rightEyeNodeIndex.HasValue;

    /// <summary>Set look-at angles directly (radians). Yaw + = right, Pitch + = up.</summary>
    public void SetAngles(float yaw, float pitch)
    {
        _yaw   = Math.Clamp(yaw,   -MaxYaw,   MaxYaw);
        _pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);
    }

    /// <summary>
    /// Convert a normalized screen position (0-1, origin top-left) to look-at angles.
    /// Screen center = forward gaze.
    /// </summary>
    public void SetScreenPosition(float normalizedX, float normalizedY)
    {
        SetAngles(
            yaw:   (normalizedX - 0.5f) *  2f * MaxYaw,
            pitch: (normalizedY - 0.5f) * -2f * MaxPitch); // screen-Y down → negative pitch
    }

    /// <summary>Reset to forward gaze.</summary>
    public void Clear() => SetAngles(0f, 0f);

    /// <summary>
    /// Injects eye-bone rotation overrides into <paramref name="overrides"/>.
    /// Composes with existing override rotation when present.
    /// </summary>
    public void ApplyOverrides(Dictionary<int, GltfNodeOverride> overrides)
    {
        if (!IsEnabled || (_yaw == 0f && _pitch == 0f)) return;

        var rot = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);

        if (_leftEyeNodeIndex.HasValue)
            overrides[_leftEyeNodeIndex.Value] = ComposeRotation(overrides, _leftEyeNodeIndex.Value, rot);
        if (_rightEyeNodeIndex.HasValue)
            overrides[_rightEyeNodeIndex.Value] = ComposeRotation(overrides, _rightEyeNodeIndex.Value, rot);
    }

    private static GltfNodeOverride ComposeRotation(
        Dictionary<int, GltfNodeOverride> overrides, int nodeIndex, Quaternion lookAtRot)
    {
        if (overrides.TryGetValue(nodeIndex, out var existing) && existing.Rotation.HasValue)
            return existing with { Rotation = existing.Rotation.Value * lookAtRot };
        return new GltfNodeOverride { Rotation = lookAtRot };
    }
}
