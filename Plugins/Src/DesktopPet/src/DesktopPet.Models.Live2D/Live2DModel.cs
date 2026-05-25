using System.Runtime.InteropServices;

namespace DesktopPet.Models.Live2D;

public unsafe sealed class Live2DModel : IDisposable
{
    private const byte ConstantFlagDoubleSidedMask = 1 << 2;
    private const byte ConstantFlagInvertedMask = 1 << 3;
    private const byte DynamicFlagVisibleMask = 1 << 0;

    private readonly AlignedBuffer _mocMemory;
    private readonly AlignedBuffer _modelMemory;
    private readonly Lock _sync = new();
    private readonly Live2DMotion? _idleMotion;
    private readonly nint _model;
    private bool _disposed;

    private Live2DModel(
        Live2DModelInfo info,
        AlignedBuffer mocMemory,
        AlignedBuffer modelMemory,
        nint model,
        Live2DMotion? idleMotion)
    {
        Info = info;
        _mocMemory = mocMemory;
        _modelMemory = modelMemory;
        _model = model;
        _idleMotion = idleMotion;
    }

    public Live2DModelInfo Info { get; }

    public static Live2DModel Create(
        string name,
        string modelJsonPath,
        string mocPath,
        IReadOnlyList<string> texturePaths,
        Live2DPoseManifest? pose,
        Live2DMotion? idleMotion)
    {
        if (!File.Exists(mocPath))
        {
            throw new FileNotFoundException("Live2D moc3 file was not found.", mocPath);
        }

        foreach (var texturePath in texturePaths)
        {
            if (!File.Exists(texturePath))
            {
                throw new FileNotFoundException("Live2D texture file was not found.", texturePath);
            }
        }

        var mocBytes = File.ReadAllBytes(mocPath);
        var mocMemory = AlignedBuffer.CopyFrom(mocBytes, 64);

        try
        {
            if (CubismCoreNative.HasMocConsistency(mocMemory.Pointer, (uint)mocBytes.Length) == 0)
            {
                throw new InvalidOperationException($"Live2D moc3 consistency check failed: {mocPath}");
            }

            var moc = CubismCoreNative.ReviveMocInPlace(mocMemory.Pointer, (uint)mocBytes.Length);
            if (moc == 0)
            {
                throw new InvalidOperationException($"Live2D moc revive failed: {mocPath}");
            }

            var modelSize = CubismCoreNative.GetSizeofModel(moc);
            var modelMemory = AlignedBuffer.Allocate(modelSize, 16);

            try
            {
                var model = CubismCoreNative.InitializeModelInPlace(moc, modelMemory.Pointer, modelSize);
                if (model == 0)
                {
                    throw new InvalidOperationException($"Live2D model initialization failed: {mocPath}");
                }

                var parameterCount = CubismCoreNative.GetParameterCount(model);
                var drawableCount = CubismCoreNative.GetDrawableCount(model);
                var mouthParameterIndex = FindParameterIndex(model, parameterCount, "ParamMouthOpenY");
                ApplyInitialPose(model, pose);
                CubismCoreNative.UpdateModel(model);
                var info = new Live2DModelInfo(
                    name,
                    modelJsonPath,
                    mocPath,
                    texturePaths,
                    parameterCount,
                    drawableCount,
                    mouthParameterIndex);

                return new Live2DModel(info, mocMemory, modelMemory, model, idleMotion);
            }
            catch
            {
                modelMemory.Dispose();
                throw;
            }
        }
        catch
        {
            mocMemory.Dispose();
            throw;
        }
    }

    public void SetMouthOpen(float value)
    {
        ThrowIfDisposed();

        if (Info.MouthParameterIndex < 0)
        {
            return;
        }

        lock (_sync)
        {
            var values = (float*)CubismCoreNative.GetParameterValues(_model);
            values[Info.MouthParameterIndex] = Math.Clamp(value, 0.0f, 1.0f);
            CubismCoreNative.UpdateModel(_model);
        }
    }

    public void AdvanceMotion(float elapsedSeconds)
    {
        ThrowIfDisposed();

        if (_idleMotion is null || _idleMotion.Tracks.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            ApplyMotion(_idleMotion, elapsedSeconds);
            CubismCoreNative.UpdateModel(_model);
        }
    }

    public IReadOnlyList<Live2DDrawableSnapshot> ReadDrawableSnapshots(int maxCount = int.MaxValue)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            return ReadDrawableSnapshotsCore(maxCount);
        }
    }

    public float? TryGetParameterValue(string parameterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        ThrowIfDisposed();

        lock (_sync)
        {
            var parameterCount = CubismCoreNative.GetParameterCount(_model);
            var parameterIndex = FindParameterIndex(_model, parameterCount, parameterId);
            if (parameterIndex < 0)
            {
                return null;
            }

            var values = (float*)CubismCoreNative.GetParameterValues(_model);
            return values[parameterIndex];
        }
    }

    public IReadOnlyList<string> ReadVisibleDrawableIds()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            var drawableCount = CubismCoreNative.GetDrawableCount(_model);
            var ids = (nint*)CubismCoreNative.GetDrawableIds(_model);
            var dynamicFlags = (byte*)CubismCoreNative.GetDrawableDynamicFlags(_model);
            var visibleIds = new List<string>();

            for (var i = 0; i < drawableCount; i++)
            {
                if ((dynamicFlags[i] & DynamicFlagVisibleMask) != 0)
                {
                    visibleIds.Add(Marshal.PtrToStringAnsi(ids[i]) ?? string.Empty);
                }
            }

            return visibleIds;
        }
    }

    private IReadOnlyList<Live2DDrawableSnapshot> ReadDrawableSnapshotsCore(int maxCount)
    {
        var drawableCount = Math.Min(Info.DrawableCount, maxCount);
        var ids = (nint*)CubismCoreNative.GetDrawableIds(_model);
        var partIds = (nint*)CubismCoreNative.GetPartIds(_model);
        var textureIndices = (int*)CubismCoreNative.GetDrawableTextureIndices(_model);
        var drawOrders = (int*)CubismCoreNative.GetDrawableDrawOrders(_model);
        var renderOrders = (int*)CubismCoreNative.GetRenderOrders(_model);
        var constantFlags = (byte*)CubismCoreNative.GetDrawableConstantFlags(_model);
        var dynamicFlags = (byte*)CubismCoreNative.GetDrawableDynamicFlags(_model);
        var blendModes = (int*)CubismCoreNative.GetDrawableBlendModes(_model);
        var opacities = (float*)CubismCoreNative.GetDrawableOpacities(_model);
        var partOpacities = (float*)CubismCoreNative.GetPartOpacities(_model);
        var partParentIndices = (int*)CubismCoreNative.GetPartParentPartIndices(_model);
        var drawableParentPartIndices = (int*)CubismCoreNative.GetDrawableParentPartIndices(_model);
        var maskCounts = (int*)CubismCoreNative.GetDrawableMaskCounts(_model);
        var masks = (nint*)CubismCoreNative.GetDrawableMasks(_model);
        var vertexCounts = (int*)CubismCoreNative.GetDrawableVertexCounts(_model);
        var vertexPositions = (nint*)CubismCoreNative.GetDrawableVertexPositions(_model);
        var vertexUvs = (nint*)CubismCoreNative.GetDrawableVertexUvs(_model);
        var indexCounts = (int*)CubismCoreNative.GetDrawableIndexCounts(_model);
        var indices = (nint*)CubismCoreNative.GetDrawableIndices(_model);
        var snapshots = new Live2DDrawableSnapshot[drawableCount];

        for (var i = 0; i < drawableCount; i++)
        {
            var vertexFloatCount = vertexCounts[i] * 2;
            var indexCount = indexCounts[i];
            var positions = new float[vertexFloatCount];
            var uvs = new float[vertexFloatCount];
            var drawableIndices = new ushort[indexCount];
            var maskIndices = new int[maskCounts[i]];

            new ReadOnlySpan<float>((void*)vertexPositions[i], vertexFloatCount).CopyTo(positions);
            new ReadOnlySpan<float>((void*)vertexUvs[i], vertexFloatCount).CopyTo(uvs);
            new ReadOnlySpan<ushort>((void*)indices[i], indexCount).CopyTo(drawableIndices);
            if (maskIndices.Length > 0)
            {
                new ReadOnlySpan<int>((void*)masks[i], maskIndices.Length).CopyTo(maskIndices);
            }

            var parentPartIndex = drawableParentPartIndices[i];
            var parentPartOpacity = GetParentPartOpacity(parentPartIndex, partParentIndices, partOpacities);
            snapshots[i] = new Live2DDrawableSnapshot(
                i,
                Marshal.PtrToStringAnsi(ids[i]) ?? string.Empty,
                textureIndices[i],
                parentPartIndex >= 0 ? Marshal.PtrToStringAnsi(partIds[parentPartIndex]) ?? string.Empty : string.Empty,
                parentPartOpacity,
                drawOrders[i],
                renderOrders[i],
                blendModes[i],
                (constantFlags[i] & ConstantFlagDoubleSidedMask) != 0,
                (constantFlags[i] & ConstantFlagInvertedMask) != 0,
                (dynamicFlags[i] & DynamicFlagVisibleMask) != 0,
                vertexCounts[i],
                indexCount,
                opacities[i] * parentPartOpacity,
                positions,
                uvs,
                drawableIndices,
                maskIndices);
        }

        return snapshots;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _modelMemory.Dispose();
        _mocMemory.Dispose();
    }

    private static int FindParameterIndex(nint model, int parameterCount, string parameterId)
    {
        var ids = (nint*)CubismCoreNative.GetParameterIds(model);
        for (var i = 0; i < parameterCount; i++)
        {
            if (string.Equals(Marshal.PtrToStringAnsi(ids[i]), parameterId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void ApplyMotion(Live2DMotion motion, float elapsedSeconds)
    {
        var time = ResolveMotionTime(motion, elapsedSeconds);
        var parameterCount = CubismCoreNative.GetParameterCount(_model);
        var parameterValues = (float*)CubismCoreNative.GetParameterValues(_model);
        var partCount = CubismCoreNative.GetPartCount(_model);
        var partOpacities = (float*)CubismCoreNative.GetPartOpacities(_model);

        foreach (var track in motion.Tracks)
        {
            if (track.Target == Live2DMotionTarget.Parameter)
            {
                if (string.Equals(track.Id, "ParamMouthOpenY", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameterIndex = FindParameterIndex(_model, parameterCount, track.Id);
                if (parameterIndex >= 0)
                {
                    parameterValues[parameterIndex] = EvaluateTrack(track, time);
                }
            }
            else if (track.Target == Live2DMotionTarget.PartOpacity)
            {
                var partIndex = FindPartIndex(_model, partCount, track.Id);
                if (partIndex >= 0)
                {
                    partOpacities[partIndex] = EvaluateTrack(track, time);
                }
            }
        }
    }

    private static float ResolveMotionTime(Live2DMotion motion, float elapsedSeconds)
    {
        if (motion.Duration <= 0.0f)
        {
            return 0.0f;
        }

        if (!motion.Loop)
        {
            return Math.Clamp(elapsedSeconds, 0.0f, motion.Duration);
        }

        return elapsedSeconds % motion.Duration;
    }

    private static float EvaluateTrack(Live2DMotionTrack track, float time)
    {
        if (track.KeyFrames.Count == 0)
        {
            return 0.0f;
        }

        if (time <= track.KeyFrames[0].Time)
        {
            return track.KeyFrames[0].Value;
        }

        for (var i = 1; i < track.KeyFrames.Count; i++)
        {
            var previous = track.KeyFrames[i - 1];
            var next = track.KeyFrames[i];
            if (time > next.Time)
            {
                continue;
            }

            var duration = next.Time - previous.Time;
            if (duration <= 0.0f)
            {
                return next.Value;
            }

            var amount = Math.Clamp((time - previous.Time) / duration, 0.0f, 1.0f);
            return previous.Value + ((next.Value - previous.Value) * amount);
        }

        return track.KeyFrames[^1].Value;
    }

    private static int FindPartIndex(nint model, int partCount, string partId)
    {
        var ids = (nint*)CubismCoreNative.GetPartIds(model);
        for (var i = 0; i < partCount; i++)
        {
            if (string.Equals(Marshal.PtrToStringAnsi(ids[i]), partId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void ApplyInitialPose(nint model, Live2DPoseManifest? pose)
    {
        if (pose?.Groups is not { Count: > 0 })
        {
            return;
        }

        var parameterCount = CubismCoreNative.GetParameterCount(model);
        var parameterValues = (float*)CubismCoreNative.GetParameterValues(model);
        var partCount = CubismCoreNative.GetPartCount(model);
        var partOpacities = (float*)CubismCoreNative.GetPartOpacities(model);

        foreach (var group in pose.Groups)
        {
            for (var i = 0; i < group.Count; i++)
            {
                var opacity = i == 0 ? 1.0f : 0.0f;
                ApplyPosePart(model, parameterCount, parameterValues, partCount, partOpacities, group[i].Id, opacity);

                var linkedPartIds = group[i].Link;
                if (linkedPartIds is not { Count: > 0 })
                {
                    continue;
                }

                foreach (var linkedPartId in linkedPartIds)
                {
                    ApplyPosePart(model, parameterCount, parameterValues, partCount, partOpacities, linkedPartId, opacity);
                }
            }
        }
    }

    private static void ApplyPosePart(
        nint model,
        int parameterCount,
        float* parameterValues,
        int partCount,
        float* partOpacities,
        string partId,
        float opacity)
    {
        var partIndex = FindPartIndex(model, partCount, partId);
        if (partIndex >= 0)
        {
            partOpacities[partIndex] = opacity;
        }

        var parameterIndex = FindParameterIndex(model, parameterCount, partId);
        if (parameterIndex >= 0)
        {
            parameterValues[parameterIndex] = opacity;
        }
    }

    private static float GetParentPartOpacity(int partIndex, int* partParentIndices, float* partOpacities)
    {
        if (partIndex < 0)
        {
            return 1.0f;
        }

        var opacity = 1.0f;
        var currentPartIndex = partIndex;
        while (currentPartIndex >= 0)
        {
            opacity *= Math.Clamp(partOpacities[currentPartIndex], 0.0f, 1.0f);
            currentPartIndex = partParentIndices[currentPartIndex];
        }

        return opacity;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
