using System.Buffers.Binary;
using System.Numerics;

namespace DesktopPet.Models.Gltf;

public sealed class GltfAnimationEvaluator
{
    private sealed record Channel(int NodeIndex, string Path, float[] Times, float[] Values, bool IsLinear)
    {
        public float Duration => Times.Length > 0 ? Times[Times.Length - 1] : 0f;
    }

    private readonly Channel[] _channels;

    public float Duration { get; }

    private GltfAnimationEvaluator(Channel[] channels)
    {
        _channels = channels;
        Duration = channels.Length > 0 ? channels.Max(c => c.Duration) : 0f;
    }

    internal static GltfAnimationEvaluator? Build(GltfManifest manifest, byte[] binaryChunk, int animationIndex)
    {
        var animations = manifest.Animations;
        if (animations is null || animationIndex < 0 || animationIndex >= animations.Count)
        {
            return null;
        }

        var animation = animations[animationIndex];
        if (animation.Channels is null || animation.Samplers is null)
        {
            return null;
        }

        var accessors = manifest.Accessors;
        var bufferViews = manifest.BufferViews;
        if (accessors is null || bufferViews is null)
        {
            return null;
        }

        var channels = new List<Channel>();
        foreach (var ch in animation.Channels)
        {
            if (ch.Target?.Node is null || ch.Target.Path is null)
            {
                continue;
            }

            if (ch.Sampler < 0 || ch.Sampler >= animation.Samplers.Count)
            {
                continue;
            }

            var sampler = animation.Samplers[ch.Sampler];
            var times = ReadFloatAccessor(accessors, bufferViews, binaryChunk, sampler.Input);
            var values = ReadFloatAccessor(accessors, bufferViews, binaryChunk, sampler.Output);

            if (times is null || values is null || times.Length == 0)
            {
                continue;
            }

            var isLinear = !string.Equals(sampler.Interpolation, "STEP", StringComparison.OrdinalIgnoreCase);
            channels.Add(new Channel(ch.Target.Node.Value, ch.Target.Path, times, values, isLinear));
        }

        return channels.Count > 0 ? new GltfAnimationEvaluator([.. channels]) : null;
    }

    public Dictionary<int, GltfNodeOverride> Evaluate(float timeSeconds)
    {
        var result = new Dictionary<int, GltfNodeOverride>();

        foreach (var ch in _channels)
        {
            var t = timeSeconds % MathF.Max(ch.Duration, 1e-6f);
            if (!result.TryGetValue(ch.NodeIndex, out var existing))
            {
                existing = new GltfNodeOverride();
            }

            switch (ch.Path)
            {
                case "translation":
                    existing = existing with { Translation = InterpolateVec3(ch, t) };
                    break;
                case "rotation":
                    existing = existing with { Rotation = InterpolateQuat(ch, t) };
                    break;
                case "scale":
                    existing = existing with { Scale = InterpolateVec3(ch, t) };
                    break;
            }

            result[ch.NodeIndex] = existing;
        }

        return result;
    }

    private static Vector3 InterpolateVec3(Channel ch, float t)
    {
        FindKeyframes(ch.Times, t, out var lo, out var hi, out var alpha);
        var v0 = ReadVec3(ch.Values, lo);
        if (lo == hi || !ch.IsLinear)
        {
            return v0;
        }

        return Vector3.Lerp(v0, ReadVec3(ch.Values, hi), alpha);
    }

    private static Quaternion InterpolateQuat(Channel ch, float t)
    {
        FindKeyframes(ch.Times, t, out var lo, out var hi, out var alpha);
        var q0 = ReadQuat(ch.Values, lo);
        if (lo == hi || !ch.IsLinear)
        {
            return q0;
        }

        return Quaternion.Slerp(q0, ReadQuat(ch.Values, hi), alpha);
    }

    private static void FindKeyframes(float[] times, float t, out int lo, out int hi, out float alpha)
    {
        if (times.Length == 1 || t <= times[0])
        {
            lo = hi = 0;
            alpha = 0f;
            return;
        }

        if (t >= times[times.Length - 1])
        {
            lo = hi = times.Length - 1;
            alpha = 0f;
            return;
        }

        lo = 0;
        hi = times.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] <= t)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var span = times[hi] - times[lo];
        alpha = span > 1e-9f ? (t - times[lo]) / span : 0f;
    }

    private static Vector3 ReadVec3(float[] values, int index)
    {
        var offset = index * 3;
        return new Vector3(values[offset], values[offset + 1], values[offset + 2]);
    }

    private static Quaternion ReadQuat(float[] values, int index)
    {
        var offset = index * 4;
        return new Quaternion(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]);
    }

    private static float[]? ReadFloatAccessor(
        IReadOnlyList<GltfAccessor> accessors,
        IReadOnlyList<GltfBufferView> bufferViews,
        byte[] binaryChunk,
        int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
        {
            return null;
        }

        var accessor = accessors[accessorIndex];
        if (accessor.ComponentType != 5126) // FLOAT
        {
            return null;
        }

        if (!accessor.BufferView.HasValue)
        {
            return null;
        }

        var bvIndex = accessor.BufferView.Value;
        if (bvIndex < 0 || bvIndex >= bufferViews.Count)
        {
            return null;
        }

        var bv = bufferViews[bvIndex];
        var byteOffset = (bv.ByteOffset ?? 0) + (accessor.ByteOffset ?? 0);

        var componentCount = accessor.Type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => 1
        };

        var totalFloats = accessor.Count * componentCount;
        var result = new float[totalFloats];
        var stride = bv.ByteStride ?? (componentCount * 4);

        for (var i = 0; i < accessor.Count; i++)
        {
            var offset = byteOffset + i * stride;
            for (var c = 0; c < componentCount; c++)
            {
                if (offset + c * 4 + 4 > binaryChunk.Length)
                {
                    return null;
                }

                result[i * componentCount + c] = BinaryPrimitives.ReadSingleLittleEndian(
                    binaryChunk.AsSpan(offset + c * 4, 4));
            }
        }

        return result;
    }
}

public sealed record GltfNodeOverride
{
    public Vector3? Translation { get; init; }
    public Quaternion? Rotation { get; init; }
    public Vector3? Scale { get; init; }
}
