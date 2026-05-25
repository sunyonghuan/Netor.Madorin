using System.Text.Json;

namespace DesktopPet.Models.Live2D;

public sealed class Live2DModelLoader
{
    public IReadOnlyList<string> FindModelJsonFiles(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);

        if (!Directory.Exists(modelsDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(modelsDirectory, "*.model3.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Live2DModel Load(string modelJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelJsonPath);

        var fullModelJsonPath = Path.GetFullPath(modelJsonPath);
        var baseDirectory = Path.GetDirectoryName(fullModelJsonPath)
            ?? throw new InvalidOperationException("Model JSON path has no parent directory.");

        using var stream = File.OpenRead(fullModelJsonPath);
        var manifest = JsonSerializer.Deserialize(stream, Live2DJsonContext.Default.Live2DModelManifest)
            ?? throw new InvalidOperationException($"Failed to parse Live2D model manifest: {fullModelJsonPath}");

        var mocPath = Path.GetFullPath(Path.Combine(baseDirectory, manifest.FileReferences.Moc));
        var texturePaths = manifest.FileReferences.Textures
            .Select(texture => Path.GetFullPath(Path.Combine(baseDirectory, texture)))
            .ToArray();
        var pose = LoadPose(baseDirectory, manifest.FileReferences.Pose);
        var idleMotion = LoadFirstMotion(baseDirectory, manifest.FileReferences.Motions, "Idle");

        return Live2DModel.Create(
            Path.GetFileNameWithoutExtension(fullModelJsonPath),
            fullModelJsonPath,
            mocPath,
            texturePaths,
            pose,
            idleMotion);
    }

    private static Live2DPoseManifest? LoadPose(string baseDirectory, string? poseReference)
    {
        if (string.IsNullOrWhiteSpace(poseReference))
        {
            return null;
        }

        var posePath = Path.GetFullPath(Path.Combine(baseDirectory, poseReference));
        if (!File.Exists(posePath))
        {
            throw new FileNotFoundException("Live2D pose file was not found.", posePath);
        }

        using var stream = File.OpenRead(posePath);
        return JsonSerializer.Deserialize(stream, Live2DJsonContext.Default.Live2DPoseManifest)
            ?? throw new InvalidOperationException($"Failed to parse Live2D pose manifest: {posePath}");
    }

    private static Live2DMotion? LoadFirstMotion(
        string baseDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<Live2DMotionReference>>? motions,
        string groupName)
    {
        if (motions is null || !motions.TryGetValue(groupName, out var group) || group.Count == 0)
        {
            return null;
        }

        var motionPath = Path.GetFullPath(Path.Combine(baseDirectory, group[0].File));
        if (!File.Exists(motionPath))
        {
            throw new FileNotFoundException("Live2D motion file was not found.", motionPath);
        }

        using var stream = File.OpenRead(motionPath);
        var manifest = JsonSerializer.Deserialize(stream, Live2DJsonContext.Default.Live2DMotionManifest)
            ?? throw new InvalidOperationException($"Failed to parse Live2D motion manifest: {motionPath}");

        return new Live2DMotion(
            manifest.Meta.Duration,
            manifest.Meta.Loop,
            manifest.Curves
                .Select(ToMotionTrack)
                .Where(track => track is not null)
                .Select(track => track!)
                .ToArray());
    }

    private static Live2DMotionTrack? ToMotionTrack(Live2DMotionCurve curve)
    {
        var target = curve.Target switch
        {
            "Parameter" => Live2DMotionTarget.Parameter,
            "PartOpacity" => Live2DMotionTarget.PartOpacity,
            _ => (Live2DMotionTarget?)null
        };

        if (target is null)
        {
            return null;
        }

        var keyFrames = ReadLinearKeyFrames(curve.Segments);
        return keyFrames.Count == 0 ? null : new Live2DMotionTrack(target.Value, curve.Id, keyFrames);
    }

    private static IReadOnlyList<Live2DMotionKeyFrame> ReadLinearKeyFrames(IReadOnlyList<float> segments)
    {
        if (segments.Count < 2)
        {
            return [];
        }

        var keyFrames = new List<Live2DMotionKeyFrame> { new(segments[0], segments[1]) };
        for (var i = 2; i < segments.Count;)
        {
            var segmentType = (int)segments[i++];
            if (i + 1 >= segments.Count)
            {
                break;
            }

            float nextTime;
            float nextValue;
            switch (segmentType)
            {
                case 0:
                case 2:
                case 3:
                    nextTime = segments[i];
                    nextValue = segments[i + 1];
                    i += 2;
                    break;
                case 1:
                    if (i + 5 >= segments.Count)
                    {
                        return keyFrames;
                    }

                    nextTime = segments[i + 4];
                    nextValue = segments[i + 5];
                    i += 6;
                    break;
                default:
                    return keyFrames;
            }

            keyFrames.Add(new Live2DMotionKeyFrame(nextTime, nextValue));
        }

        return keyFrames;
    }
}
